using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// Builds the shell script the marketplace runs when the container boots.
///
/// The script's order is the whole design:
///
///   1. <b>Starts the auth proxy first.</b> Installing ComfyUI and pulling
///      7–35 GB of weights takes 10–40 minutes. If nothing answered during
///      that window the studio could not tell "still warming up" from "dead
///      box". The proxy answers from second one and reports the stage.
///   2. Proves CUDA works and freezes torch behind a pip constraint.
///   3. <b>Starts the weight download in the background</b> and moves on.
///   4. Installs a pinned ComfyUI (bound to <c>127.0.0.1</c> only) while those
///      bytes are still arriving, then joins the download and verifies it.
///   5. Flips the stage to <c>ready</c> once ComfyUI actually responds, and
///      then <b>never exits</b>: it supervises ComfyUI and the proxy.
///
/// <b>What the first version got wrong, all of it only visible on a real
/// machine</b> (checked against the owner's aixman service, which rents on the
/// same vendor and image family and works):
///   * The script shipped with CRLF line endings — <c>set -uo pipefail\r</c>
///     fails and every <c>fi\r</c> leaves bash unbalanced — so nothing ran.
///   * It relied on curl, which the stock PyTorch runtime image does not
///     ship: aria2 could not be fetched, every weight download failed, and the
///     readiness check could never succeed. Python is used instead.
///   * It exited on failure. The script is the container's main process, so
///     the proxy died with it and the studio never learned why.
///   * It cloned ComfyUI's moving master branch.
///
/// <b>Why the proxy exists at all:</b> ComfyUI ships with no authentication,
/// and the rented port is reachable from the public internet. Requests must
/// carry <c>Authorization: Bearer &lt;token&gt;</c>, compared in constant time.
/// </summary>
public static class GpuProvisioning
{
    /// <summary>Container port the proxy listens on — the only one exposed.
    /// 8189, as on aixman's working workers: SimplePod's own docs use 20000
    /// as the example mapping for its console service.</summary>
    public const int ProxyPort = 8189;

    /// <summary>ComfyUI's port, bound to loopback and never exposed.</summary>
    public const int ComfyPort = 8188;

    /// <summary>Status endpoint the studio polls while a worker warms up.</summary>
    public const string StatusPath = "/chanthra/status";

    /// <summary>
    /// ComfyUI release the workers install. Pinned: nodes change between
    /// releases, and every rented machine cloning a different master would make
    /// each rental a new experiment. v0.36.0 is the tag aixman validates its
    /// graphs against on this vendor; it carries every core node the bundled
    /// workflows use (CreateVideo/SaveVideo included). Overridable with the
    /// <c>gpu:comfyRef</c> setting.
    /// </summary>
    public const string DefaultComfyRef = "v0.36.0";

    private const string ComfyRepo = "https://github.com/Comfy-Org/ComfyUI.git";

    /// <summary>Mints a 256-bit worker token. One per worker, never reused.</summary>
    public static string NewWorkerToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Assembles the boot script for one worker.
    /// </summary>
    /// <param name="profile">What to install.</param>
    /// <param name="workerToken">The bearer token this worker will require.</param>
    /// <param name="hfToken">Optional Hugging Face token for gated repos.</param>
    /// <param name="comfyRef">ComfyUI tag to install; null for <see cref="DefaultComfyRef"/>.</param>
    public static string BuildStartScript(GpuModelProfile profile, string workerToken, string? hfToken, string? comfyRef = null)
    {
        if (string.IsNullOrWhiteSpace(workerToken))
            throw new ArgumentException("A worker token is required — an unauthenticated ComfyUI on a public port is not something we ship.", nameof(workerToken));

        // aria2c's input-file format: a URL line, then its options indented
        // beneath it. One list for the whole profile means one process, one
        // exit code, and file-level parallelism for free.
        var list = new StringBuilder();
        var verify = new StringBuilder();
        foreach (var f in profile.Files)
        {
            list.Append(f.Url).Append('\n')
                .Append("  dir=").Append(ModelsDir).Append('/').Append(f.Folder).Append('\n')
                .Append("  out=").Append(f.FileName).Append('\n');

            // Single-quoted shell args; the catalog is app-controlled, but a
            // stray quote in an edited overrides file should break the script
            // loudly rather than splice into it.
            verify.Append("verify_one ")
                  .Append(ShellQuote(f.Folder)).Append(' ')
                  .Append(ShellQuote(f.FileName)).Append(' ')
                  .Append(f.SizeGb.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append('\n');
        }

        var totalBytes = (long)(profile.TotalWeightsGb * 1073741824.0);

        var script = ScriptTemplate
            .Replace("@@TOKEN@@", ShellQuote(workerToken))
            .Replace("@@HF_TOKEN@@", ShellQuote(hfToken ?? ""))
            .Replace("@@PROXY_PORT@@", ProxyPort.ToString(CultureInfo.InvariantCulture))
            .Replace("@@COMFY_PORT@@", ComfyPort.ToString(CultureInfo.InvariantCulture))
            .Replace("@@PROFILE@@", ShellQuote(profile.Key))
            .Replace("@@TOTAL_BYTES@@", totalBytes.ToString(CultureInfo.InvariantCulture))
            .Replace("@@ARIA2_URL@@", ShellQuote(Aria2StaticUrl))
            .Replace("@@COMFY_REPO@@", ShellQuote(ComfyRepo))
            .Replace("@@COMFY_REF@@", ShellQuote(string.IsNullOrWhiteSpace(comfyRef) ? DefaultComfyRef : comfyRef!.Trim()))
            .Replace("@@DOWNLOAD_LIST@@", list.ToString().TrimEnd())
            .Replace("@@VERIFY@@", verify.ToString().TrimEnd())
            .Replace("@@FETCH_SOURCE@@", FetchSource)
            .Replace("@@PROXY_SOURCE@@", ProxySource);

        // The C# source may be checked out with CRLF (core.autocrlf), and a raw
        // string literal keeps whatever the file had. Bash reads "\r" as part
        // of every line, so it must never reach the machine.
        return script.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    /// <summary>Where the worker keeps everything.</summary>
    private const string Root = "/workspace/chanthra";

    /// <summary>Weights live outside the ComfyUI checkout (the download
    /// starts before the checkout exists); ComfyUI's models/ is a link here.</summary>
    private const string ModelsDir = Root + "/models";

    /// <summary>
    /// Statically-linked aria2c, pinned to a release tag. Fetched with Python —
    /// the runtime image has no curl — and optional: without it the Python
    /// downloader below does the job on one connection per file.
    /// </summary>
    private const string Aria2StaticUrl =
        "https://github.com/abcfy2/aria2-static-build/releases/download/1.37.0/aria2-x86_64-linux-musl_static.zip";

    /// <summary>Wraps a value in single quotes, escaping any it contains.</summary>
    internal static string ShellQuote(string value)
        => "'" + (value ?? "").Replace("'", "'\\''") + "'";

    // =====================================================================
    // Boot script
    // =====================================================================

    private const string ScriptTemplate = """
#!/usr/bin/env bash
# Chanthra Studio — rented worker bootstrap. Generated by GpuProvisioning.
# NOTE: -e is deliberately omitted. A failed apt mirror or an optional step must
# not abort the boot and strand a machine that is already being billed.
set -uo pipefail
# The vendor's start-script runner need not carry the image's PATH, and in the
# stock PyTorch image python3 and pip live only under /opt/conda/bin.
export PATH="/opt/conda/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin${PATH:+:$PATH}"

CH_HOME=/workspace/chanthra
COMFY=$CH_HOME/ComfyUI
MODELS=$CH_HOME/models
mkdir -p "$CH_HOME" "$MODELS"
exec >>"$CH_HOME/boot.log" 2>&1
echo "=== chanthra worker boot $(date -u '+%Y-%m-%dT%H:%M:%SZ') ==="

export CHANTHRA_TOKEN=@@TOKEN@@
export CHANTHRA_PROXY_PORT=@@PROXY_PORT@@
export CHANTHRA_COMFY_PORT=@@COMFY_PORT@@
export CHANTHRA_PROFILE=@@PROFILE@@
export CHANTHRA_HOME="$CH_HOME"
export HF_TOKEN=@@HF_TOKEN@@
TOTAL_BYTES=@@TOTAL_BYTES@@

stage()  { printf '%s' "$1" > "$CH_HOME/stage";  echo "[stage] $1"; }
detail() { printf '%s' "$1" > "$CH_HOME/detail"; }
# A failure is recorded, never exited on: this script is the container's main
# process, and exiting would take the proxy — the only thing that can tell the
# studio what went wrong — down with it.
FAILED=0
fail()   { [ "$FAILED" = 1 ] && return; FAILED=1; printf '%s' "$1" > "$CH_HOME/error"; stage failed; echo "[fail] $1"; }

: > "$CH_HOME/error"
detail ""
stage booting

PY="$(command -v python3 || command -v python || true)"
if [ -z "$PY" ]; then
  echo "[fail] no python interpreter in the image"
  printf '%s' "no python interpreter in the image" > "$CH_HOME/error"; stage failed
  while true; do sleep 3600; done
fi

# --- 1. auth proxy, before anything slow -----------------------------------
cat > "$CH_HOME/proxy.py" <<'CHANTHRA_PROXY_EOF'
@@PROXY_SOURCE@@
CHANTHRA_PROXY_EOF

start_proxy() {
  "$PY" "$CH_HOME/proxy.py" >>"$CH_HOME/proxy.log" 2>&1 &
  PROXY_PID=$!
  echo "[boot] proxy pid $PROXY_PID"
}
start_proxy

# --- 2. sanity + pip guard -------------------------------------------------
stage deps
# A host whose driver is older than the image's CUDA boots the container fine
# and only fails at the first tensor — find out now, not 30 minutes in.
if ! "$PY" -c "import sys, torch; sys.exit(0 if torch.cuda.is_available() else 1)"; then
  fail "CUDA is unavailable in the container: the host driver is older than the image's CUDA, or no GPU was attached"
fi
echo "[boot] torch $("$PY" -c 'import torch;print(torch.__version__, torch.version.cuda)' 2>/dev/null || echo '?')"

export PIP_BREAK_SYSTEM_PACKAGES=1
export PIP_ROOT_USER_ACTION=ignore
export PIP_DISABLE_PIP_VERSION_CHECK=1
# Freeze the image's CUDA-matched torch for every later pip call, so a
# dependency asking for another torch fails loudly instead of quietly pulling
# gigabytes of generic wheels over a working stack.
"$PY" -m pip list --format=freeze 2>/dev/null \
  | grep -iE '^(torch|torchvision|torchaudio|triton|nvidia-[a-z0-9-]+)==' \
  > "$CH_HOME/constraints.txt" || true
export PIP_CONSTRAINT="$CH_HOME/constraints.txt"

# Python downloader: the image has no curl, and aria2c (below) is optional.
cat > "$CH_HOME/fetch.py" <<'CHANTHRA_FETCH_EOF'
@@FETCH_SOURCE@@
CHANTHRA_FETCH_EOF

if "$PY" "$CH_HOME/fetch.py" --one @@ARIA2_URL@@ /tmp/aria2.zip \
   && "$PY" -c "import zipfile;zipfile.ZipFile('/tmp/aria2.zip').extractall('/usr/local/bin')"; then
  chmod +x /usr/local/bin/aria2c 2>/dev/null || true
fi
if command -v aria2c >/dev/null 2>&1; then
  echo "[boot] $(aria2c --version 2>/dev/null | head -1)"
else
  echo "[boot] aria2c unavailable — using the Python downloader"
fi

# --- 3. weights, IN THE BACKGROUND -----------------------------------------
stage weights
cat > "$CH_HOME/downloads.txt" <<'CHANTHRA_DL_EOF'
@@DOWNLOAD_LIST@@
CHANTHRA_DL_EOF

download_all() {
  if command -v aria2c >/dev/null 2>&1; then
    aria2c -i "$CH_HOME/downloads.txt" \
           -j 4 -x 4 -s 4 -k 1M -c \
           --file-allocation=none --auto-file-renaming=false --allow-overwrite=false \
           --max-tries=10 --retry-wait=5 --timeout=60 --connect-timeout=30 \
           --console-log-level=warn --summary-interval=0 \
           ${HF_TOKEN:+--header="Authorization: Bearer $HF_TOKEN"} && return 0
    echo "[boot] aria2c failed — finishing with the Python downloader"
  fi
  "$PY" "$CH_HOME/fetch.py" --list "$CH_HOME/downloads.txt"
}

download_all > "$CH_HOME/download.log" 2>&1 &
DL_PID=$!
echo "[boot] downloads pid $DL_PID"

# Real progress: bytes on disk against the catalog total.
progress_watch() {
  while kill -0 "$DL_PID" 2>/dev/null; do
    got=$(du -sb "$MODELS" 2>/dev/null | cut -f1)
    got=${got:-0}
    if [ "${TOTAL_BYTES:-0}" -gt 0 ]; then
      detail "$(awk -v g="$got" -v t="$TOTAL_BYTES" \
        'BEGIN{printf "weights %.1f / %.1f GB (%d%%)", g/1073741824, t/1073741824, (g*100)/t}')"
    fi
    sleep 5
  done
}
progress_watch &
WATCH_PID=$!

# --- 4. ComfyUI, pinned, while the weights land ----------------------------
export DEBIAN_FRONTEND=noninteractive
if ! command -v git >/dev/null 2>&1; then
  apt-get update -qq >/dev/null 2>&1 || true
  apt-get install -y -qq --no-install-recommends git ca-certificates >/dev/null 2>&1 || true
fi
if [ "$FAILED" = 0 ] && ! command -v git >/dev/null 2>&1; then fail "git unavailable — cannot fetch ComfyUI"; fi

if [ "$FAILED" = 0 ] && [ ! -f "$COMFY/main.py" ]; then
  rm -rf "$COMFY"
  git clone --depth 1 --branch @@COMFY_REF@@ @@COMFY_REPO@@ "$COMFY" \
    || fail "could not clone ComfyUI @@COMFY_REF@@"
fi
if [ -f "$COMFY/main.py" ]; then
  # The weights land outside the checkout (the download started before it
  # existed); point ComfyUI's model folders at them.
  rm -rf "$COMFY/models"
  ln -sfn "$MODELS" "$COMFY/models"
  # --only-binary stops a stray source distribution from building against
  # torch, which is the other way a CUDA stack gets quietly replaced.
  if [ "$FAILED" = 0 ] && ! "$PY" -m pip install --no-cache-dir -q --only-binary=:all: -r "$COMFY/requirements.txt" > "$CH_HOME/pip.log" 2>&1; then
    cat "$CH_HOME/pip.log"
    fail "ComfyUI requirements failed to install: $(grep -iE 'error|conflict|no matching' "$CH_HOME/pip.log" | tail -n 3 | tr '\n' ' ' | cut -c1-400)"
  fi
fi

# --- 5. join the downloads -------------------------------------------------
wait "$DL_PID"; DL_RC=$?
kill "$WATCH_PID" 2>/dev/null || true
if [ "$DL_RC" -ne 0 ]; then
  fail "weight download failed (exit $DL_RC) — see download.log"
fi

# A gated repo or an expired link answers 200 with an HTML error page, which
# lands as a .safetensors and only fails hours later inside a render. Compare
# against the size the catalog measured and refuse anything short.
verify_one() {
  sub="$1"; name="$2"; want_gb="$3"
  dir="$MODELS/$sub"
  got=$(stat -c %s "$dir/$name" 2>/dev/null || echo 0)
  min=$(awk -v g="$want_gb" 'BEGIN{printf "%d", g*1073741824*0.9}')
  if [ "$got" -lt "$min" ]; then
    fail "$name is $got bytes, expected ~${want_gb}GB — the URL may be gated or moved"
    return
  fi
  # ComfyUI has renamed these folders across versions and different nodes look
  # in different ones. Link rather than copy so it costs no disk.
  case "$sub" in
    diffusion_models) mkdir -p "$MODELS/unet" && ln -sf "$dir/$name" "$MODELS/unet/$name" ;;
    text_encoders)    mkdir -p "$MODELS/clip" && ln -sf "$dir/$name" "$MODELS/clip/$name" ;;
  esac
}

if [ "$FAILED" = 0 ]; then
  :
@@VERIFY@@
fi

detail ""

# --- 6. run, then supervise forever -----------------------------------------
COMFY_PID=""
start_comfy() {
  cd "$COMFY"
  "$PY" main.py --listen 127.0.0.1 --port "$CHANTHRA_COMFY_PORT" >>"$CH_HOME/comfy.log" 2>&1 &
  COMFY_PID=$!
  echo "[boot] comfyui pid $COMFY_PID"
}

comfy_answers() {
  "$PY" -c "import urllib.request,sys; urllib.request.urlopen('http://127.0.0.1:$CHANTHRA_COMFY_PORT/system_stats', timeout=5)" >/dev/null 2>&1
}

if [ "$FAILED" = 0 ]; then
  stage starting
  start_comfy
  # Only report ready once ComfyUI actually answers. Saying "ready" when the
  # process merely started would hand the studio a worker that 502s on submit.
  for i in $(seq 1 180); do
    if comfy_answers; then stage ready; break; fi
    if ! kill -0 "$COMFY_PID" 2>/dev/null; then
      fail "ComfyUI exited during startup: $(tail -n 5 "$CH_HOME/comfy.log" | tr '\n' ' ' | cut -c1-400)"
      break
    fi
    sleep 5
  done
  if [ "$FAILED" = 0 ] && [ "$(cat "$CH_HOME/stage" 2>/dev/null)" != "ready" ]; then
    fail "ComfyUI did not answer within 15 minutes of starting"
  fi
fi

# Keep PID 1 alive: if this script exits the container stops and the rental is
# wasted. Services are tracked by PID rather than pgrep, which a runtime image
# need not ship.
restarts=0
while true; do
  if [ "$FAILED" = 0 ] && [ -n "$COMFY_PID" ] && ! kill -0 "$COMFY_PID" 2>/dev/null; then
    restarts=$((restarts + 1))
    if [ "$restarts" -gt 5 ]; then
      fail "ComfyUI keeps exiting: $(tail -n 5 "$CH_HOME/comfy.log" | tr '\n' ' ' | cut -c1-400)"
    else
      echo "[boot] ComfyUI exited, restarting ($restarts)"
      stage starting
      start_comfy
      for i in $(seq 1 60); do comfy_answers && { stage ready; break; }; sleep 5; done
    fi
  fi
  if ! kill -0 "$PROXY_PID" 2>/dev/null; then
    echo "[boot] proxy exited, restarting"
    start_proxy
  fi
  sleep 20
done
""";

    // =====================================================================
    // Python downloader (standard library only)
    // =====================================================================

    // Five-quote delimiter so the Python may contain triple-quoted strings.
    private const string FetchSource = """""
# Chanthra Studio worker downloader. Resumes partial files with Range, follows
# Hugging Face's redirects, and sends the HF token when one is set.
import os, sys, time, urllib.request

TOKEN = os.environ.get("HF_TOKEN", "")

def fetch(url, dest):
    os.makedirs(os.path.dirname(dest) or ".", exist_ok=True)
    part = dest + ".part"
    for attempt in range(1, 6):
        try:
            have = os.path.getsize(part) if os.path.exists(part) else 0
            req = urllib.request.Request(url, headers={"User-Agent": "chanthra-worker/1.0"})
            if TOKEN and "huggingface.co" in url:
                req.add_header("Authorization", "Bearer " + TOKEN)
            if have:
                req.add_header("Range", "bytes=%d-" % have)
            with urllib.request.urlopen(req, timeout=60) as resp:
                mode = "ab" if have and resp.status == 206 else "wb"
                with open(part, mode) as out:
                    while True:
                        chunk = resp.read(1 << 20)
                        if not chunk:
                            break
                        out.write(chunk)
            os.replace(part, dest)
            print("[fetch] done", dest, flush=True)
            return True
        except Exception as e:
            print("[fetch] %s attempt %d failed: %s" % (url, attempt, e), flush=True)
            time.sleep(5 * attempt)
    return False

def from_list(path):
    ok, url, d, o = True, None, None, None
    with open(path) as fh:
        for raw in fh:
            line = raw.rstrip("\n")
            if line.startswith("  dir="):
                d = line[6:]
            elif line.startswith("  out="):
                o = line[6:]
                target = os.path.join(d, o)
                if os.path.exists(target) and os.path.getsize(target) > 0:
                    continue
                ok = fetch(url, target) and ok
            elif line.strip():
                url = line.strip()
    return ok

if __name__ == "__main__":
    if sys.argv[1] == "--one":
        sys.exit(0 if fetch(sys.argv[2], sys.argv[3]) else 1)
    sys.exit(0 if from_list(sys.argv[2]) else 1)
""""";

    // =====================================================================
    // Auth proxy (Python 3 standard library only — no pip at boot time)
    // =====================================================================

    private const string ProxySource = """""
# Chanthra Studio worker proxy.
#
# The only publicly reachable listener on this box. Every request is checked
# for the bearer token (constant time) before anything reaches ComfyUI, which
# listens on loopback only.
#
# One request per connection. The vendor's Cloudflare tunnel keeps a pool of
# connections to this port and reuses them for different callers, so a relay
# that authenticated only the first request of a connection would let a later,
# token-less request ride on it. Each forwarded request is therefore sent to
# ComfyUI with "Connection: close" and exactly its declared body, and only the
# reply is relayed back. A WebSocket upgrade (the /ws progress feed) is the one
# exception: once upgraded the connection belongs to that socket alone, so its
# bytes are pumped both ways.
import hmac
import json
import os
import select
import socket
import socketserver
import sys
import threading

TOKEN = os.environ.get("CHANTHRA_TOKEN", "").encode()
LISTEN_PORT = int(os.environ.get("CHANTHRA_PROXY_PORT", "8189"))
COMFY_PORT = int(os.environ.get("CHANTHRA_COMFY_PORT", "8188"))
PROFILE = os.environ.get("CHANTHRA_PROFILE", "")
HOME = os.environ.get("CHANTHRA_HOME", "/workspace/chanthra")
MAX_HEAD = 65536
MAX_CONNECTIONS = 64
SLOTS = threading.BoundedSemaphore(MAX_CONNECTIONS)
HOP = {b"connection", b"keep-alive", b"proxy-connection", b"proxy-authorization", b"te", b"trailer"}


def _read_file(name):
    try:
        with open(os.path.join(HOME, name), "r") as fh:
            return fh.read().strip()
    except OSError:
        return ""


def status_body():
    return json.dumps({
        "stage": _read_file("stage") or "booting",
        "detail": _read_file("detail"),
        "error": _read_file("error"),
        "profile": PROFILE,
        "comfyPort": COMFY_PORT,
    }).encode()


def respond(sock, code, reason, body, ctype="application/json"):
    head = (
        "HTTP/1.1 %d %s\r\n"
        "Content-Type: %s\r\n"
        "Content-Length: %d\r\n"
        "Connection: close\r\n"
        "\r\n" % (code, reason, ctype, len(body))
    ).encode()
    try:
        sock.sendall(head + body)
    except OSError:
        pass


def read_head(sock):
    """Read up to the end of the request headers. Returns (head, leftover)."""
    buf = b""
    while b"\r\n\r\n" not in buf:
        try:
            chunk = sock.recv(4096)
        except OSError:
            return None, b""
        if not chunk:
            return None, b""
        buf += chunk
        if len(buf) > MAX_HEAD:
            return None, b""
    head, rest = buf.split(b"\r\n\r\n", 1)
    return head, rest


def header(lines, name):
    for line in lines[1:]:
        if line[:len(name) + 1].lower() == name + b":":
            return line.split(b":", 1)[1].strip()
    return None


def authorized(lines):
    # An empty token would make compare_digest trivially satisfiable, so an
    # unconfigured worker refuses everything rather than opening up.
    if not TOKEN:
        return False
    supplied = header(lines, b"authorization") or b""
    return hmac.compare_digest(supplied, b"Bearer " + TOKEN)


def pump(a, b):
    """Relay bytes both ways until either side hangs up (WebSocket only)."""
    socks = [a, b]
    while True:
        readable, _, broken = select.select(socks, [], socks, 600)
        if broken or not readable:
            return
        for s in readable:
            other = b if s is a else a
            try:
                data = s.recv(65536)
            except OSError:
                return
            if not data:
                return
            try:
                other.sendall(data)
            except OSError:
                return


def relay_reply(upstream, client):
    while True:
        try:
            data = upstream.recv(65536)
        except OSError:
            return
        if not data:
            return
        try:
            client.sendall(data)
        except OSError:
            return


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        if not SLOTS.acquire(blocking=False):
            respond(self.request, 503, "Busy", b'{"error":"too many connections"}')
            return
        try:
            self.serve()
        finally:
            SLOTS.release()

    def serve(self):
        client = self.request
        # A caller gets a short while to send its headers before it is dropped.
        client.settimeout(15)

        head, rest = read_head(client)
        if head is None:
            return
        lines = head.split(b"\r\n")

        if not authorized(lines):
            respond(client, 401, "Unauthorized", b'{"error":"bad or missing bearer token"}')
            return

        try:
            method, path = lines[0].split(b" ")[0:2]
        except ValueError:
            respond(client, 400, "Bad Request", b'{"error":"malformed request line"}')
            return

        if path.startswith(b"/chanthra/status"):
            respond(client, 200, "OK", status_body())
            return

        try:
            upstream = socket.create_connection(("127.0.0.1", COMFY_PORT), timeout=10)
        except OSError:
            # ComfyUI is not up yet. Answering with the warm-up stage (rather
            # than a bare connection error) is what lets the studio show real
            # progress instead of guessing whether the box is alive.
            respond(client, 503, "Service Unavailable", status_body())
            return

        try:
            upgrade = (header(lines, b"upgrade") or b"").lower() == b"websocket"
            if upgrade:
                upstream.sendall(head + b"\r\n\r\n" + rest)
                client.settimeout(None)
                upstream.settimeout(None)
                pump(client, upstream)
                return

            # Rebuild the head with Connection: close and without the token.
            kept = [lines[0]] + [l for l in lines[1:]
                                 if l.split(b":", 1)[0].strip().lower() not in HOP
                                 and l.split(b":", 1)[0].strip().lower() != b"authorization"]
            kept.append(b"Connection: close")
            length = int(header(lines, b"content-length") or b"0")
            body = rest[:length]
            client.settimeout(120)
            while len(body) < length:
                chunk = client.recv(min(65536, length - len(body)))
                if not chunk:
                    return
                body += chunk
            upstream.sendall(b"\r\n".join(kept) + b"\r\n\r\n" + body)
            # Renders can take minutes between request and reply.
            upstream.settimeout(None)
            client.settimeout(None)
            relay_reply(upstream, client)
        except (OSError, ValueError):
            pass
        finally:
            for s in (upstream, client):
                try:
                    s.close()
                except OSError:
                    pass


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    if not TOKEN:
        sys.stderr.write("CHANTHRA_TOKEN is empty — refusing to start an open proxy\n")
        sys.exit(1)
    Server(("0.0.0.0", LISTEN_PORT), Handler).serve_forever()
""""";
}
