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
///      box", and would either give up on a machine that was fine or keep
///      paying for one that wasn't. The proxy answers from second one and
///      reports the current stage.
///   2. Proves torch is present and freezes it behind a pip constraint.
///   3. <b>Starts the weight download in the background</b> and moves on.
///   4. Installs ComfyUI (bound to <c>127.0.0.1</c> only) while those bytes
///      are still arriving, then joins the download and verifies each file.
///   5. Flips the stage to <c>ready</c> once ComfyUI actually responds.
///
/// <b>Why 3 and 4 overlap.</b> Downloading is network-bound and installing is
/// CPU- and disk-bound; running them in sequence, as the first version did,
/// spent five to eight paid minutes doing one while the other resource sat
/// idle. Overlapping them costs nothing and is the single cheapest saving in
/// the warm-up. The rest of the saving comes from aria2c: a single TCP stream
/// to a CDN PoP is latency-and-loss limited long before the NIC is, so the
/// same link that gives ~45 MB/s on one connection can give several times
/// that across sixteen.
///
/// <b>Why the proxy exists at all:</b> ComfyUI ships with no authentication,
/// and the rented port is reachable from the public internet. Anyone who
/// guessed the URL could queue work on a GPU we are paying for — and ComfyUI
/// can read and write files and install custom nodes, so it is not merely a
/// billing problem. The proxy is the only thing listening publicly; ComfyUI
/// binds to loopback. Requests must carry
/// <c>Authorization: Bearer &lt;token&gt;</c>, compared in constant time.
///
/// The proxy is deliberately a dumb TCP relay after the auth gate: it never
/// parses bodies. That is what lets it carry chunked history responses,
/// multi-gigabyte <c>/view</c> downloads, multipart image uploads, and the
/// <c>/ws</c> progress socket without special-casing any of them.
/// </summary>
public static class GpuProvisioning
{
    /// <summary>Container port the proxy listens on — the only one exposed.</summary>
    public const int ProxyPort = 20000;

    /// <summary>ComfyUI's port, bound to loopback and never exposed.</summary>
    public const int ComfyPort = 8188;

    /// <summary>Status endpoint the studio polls while a worker warms up.</summary>
    public const string StatusPath = "/chanthra/status";

    /// <summary>Mints a 256-bit worker token. One per worker, never reused.</summary>
    public static string NewWorkerToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Assembles the boot script for one worker.
    /// </summary>
    /// <param name="profile">What to install.</param>
    /// <param name="workerToken">The bearer token this worker will require.</param>
    /// <param name="hfToken">Optional Hugging Face token for gated repos.</param>
    public static string BuildStartScript(GpuModelProfile profile, string workerToken, string? hfToken)
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
                .Append("  dir=").Append(ComfyModelsDir).Append('/').Append(f.Folder).Append('\n')
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

        return ScriptTemplate
            .Replace("@@TOKEN@@", ShellQuote(workerToken))
            .Replace("@@HF_TOKEN@@", ShellQuote(hfToken ?? ""))
            .Replace("@@PROXY_PORT@@", ProxyPort.ToString(CultureInfo.InvariantCulture))
            .Replace("@@COMFY_PORT@@", ComfyPort.ToString(CultureInfo.InvariantCulture))
            .Replace("@@PROFILE@@", ShellQuote(profile.Key))
            .Replace("@@TOTAL_BYTES@@", totalBytes.ToString(CultureInfo.InvariantCulture))
            .Replace("@@ARIA2_URL@@", ShellQuote(Aria2StaticUrl))
            .Replace("@@DOWNLOAD_LIST@@", list.ToString().TrimEnd())
            .Replace("@@VERIFY@@", verify.ToString().TrimEnd())
            .Replace("@@PROXY_SOURCE@@", ProxySource);
    }

    /// <summary>Where ComfyUI's model folders live on the worker.</summary>
    private const string ComfyModelsDir = "/opt/ComfyUI/models";

    /// <summary>
    /// Statically-linked aria2c, pinned to a release tag.
    ///
    /// Fetched instead of apt-installed because <c>apt-get update</c> on a cold
    /// container pulls tens of megabytes of package indexes before it can
    /// install a 372 kB package — this is one 5.4 MB HTTPS GET, about a second,
    /// and it has no glibc or OpenSSL dependency to disagree with the image.
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
set -uo pipefail

CH_HOME=/opt/chanthra
COMFY=/opt/ComfyUI
mkdir -p "$CH_HOME"
exec >>"$CH_HOME/boot.log" 2>&1
echo "=== chanthra worker boot $(date -u '+%Y-%m-%dT%H:%M:%SZ') ==="

export CHANTHRA_TOKEN=@@TOKEN@@
export CHANTHRA_PROXY_PORT=@@PROXY_PORT@@
export CHANTHRA_COMFY_PORT=@@COMFY_PORT@@
export CHANTHRA_PROFILE=@@PROFILE@@
export HF_TOKEN=@@HF_TOKEN@@
TOTAL_BYTES=@@TOTAL_BYTES@@

stage()  { printf '%s' "$1" > "$CH_HOME/stage";  echo "[stage] $1"; }
detail() { printf '%s' "$1" > "$CH_HOME/detail"; }
fail()   { printf '%s' "$1" > "$CH_HOME/error"; stage failed; echo "[fail] $1"; }

: > "$CH_HOME/error"
detail ""
stage booting

PY="$(command -v python3 || command -v python)"
if [ -z "$PY" ]; then fail "no python interpreter in the image"; exit 1; fi

# --- 1. auth proxy, before anything slow -----------------------------------
cat > "$CH_HOME/proxy.py" <<'CHANTHRA_PROXY_EOF'
@@PROXY_SOURCE@@
CHANTHRA_PROXY_EOF

"$PY" "$CH_HOME/proxy.py" >>"$CH_HOME/proxy.log" 2>&1 &
echo "[boot] proxy pid $!"

# --- 2. sanity + pip guard -------------------------------------------------
# The image is supposed to arrive with a CUDA build of torch already in it.
# Finding out otherwise 30 minutes later, inside a render, is the expensive
# way to learn that a Docker tag was wrong.
stage deps
TORCH_VER="$("$PY" -c 'import torch;print(torch.__version__)' 2>/dev/null || true)"
if [ -z "$TORCH_VER" ]; then
  fail "no torch in this image — the profile's Docker image is wrong or failed to pull"
  exit 1
fi
echo "[boot] torch $TORCH_VER cuda $("$PY" -c 'import torch;print(torch.version.cuda)' 2>/dev/null || echo '?')"

# Freeze whatever CUDA-matched torch the image shipped, and apply it to EVERY
# later pip invocation via PIP_CONSTRAINT — including custom-node installs we
# do not control. Without this, one node pinning torch>=2.7 quietly pulls ~3 GB
# of generic torch + nvidia wheels over the GPU-matched ones: minutes of paid
# warm-up spent replacing a working CUDA stack with a worse one. A constraint
# turns that into an instant, legible resolver error instead.
mkdir -p "$CH_HOME/pipguard"
"$PY" -m pip list --format=freeze 2>/dev/null \
  | grep -E '^(torch|torchvision|torchaudio|triton|nvidia-[a-z0-9-]+)==' \
  > "$CH_HOME/pipguard/constraints.txt" || true
export PIP_CONSTRAINT="$CH_HOME/pipguard/constraints.txt"
echo "[boot] pip constraints: $(wc -l < "$CH_HOME/pipguard/constraints.txt") pinned"

# aria2c: one static musl binary, no apt, no shared-library argument with the
# image. Multi-connection + multi-file, and it resumes a part-file even when
# the control file is gone.
if ! command -v aria2c >/dev/null 2>&1; then
  if curl -fsSL --retry 3 --connect-timeout 30 -o /tmp/aria2.zip @@ARIA2_URL@@ \
     && "$PY" -c "import zipfile;zipfile.ZipFile('/tmp/aria2.zip').extractall('/usr/local/bin')"; then
    chmod +x /usr/local/bin/aria2c 2>/dev/null || true
  fi
fi
if command -v aria2c >/dev/null 2>&1; then
  echo "[boot] $(aria2c --version 2>/dev/null | head -1)"
else
  echo "[boot] aria2c unavailable — falling back to curl"
fi

# --- 3. weights, IN THE BACKGROUND -----------------------------------------
# Downloading is network-bound and installing is CPU/disk-bound, and the old
# script ran them one after the other. Overlapping them is the cheapest minutes
# in the whole warm-up — they are simply free.
stage weights
mkdir -p "$CH_HOME"
cat > "$CH_HOME/downloads.txt" <<'CHANTHRA_DL_EOF'
@@DOWNLOAD_LIST@@
CHANTHRA_DL_EOF

download_all() {
  if command -v aria2c >/dev/null 2>&1; then
    # -j 4 files x -x 4 connections = 16 sockets. Past roughly that the gain
    # flattens and connection resets start; the CDN's rate limit is per
    # 5-minute window and is not the binding constraint at this fan-out.
    aria2c -i "$CH_HOME/downloads.txt" \
           -j 4 -x 4 -s 4 -k 1M -c \
           --file-allocation=none --auto-file-renaming=false --allow-overwrite=false \
           --max-tries=10 --retry-wait=5 --timeout=60 --connect-timeout=30 \
           --console-log-level=warn --summary-interval=0 \
           ${HF_TOKEN:+--header="Authorization: Bearer $HF_TOKEN"}
    return $?
  fi
  # Fallback: the original single-stream curl, one file at a time. Slower, but
  # a box without aria2c should still warm up rather than refuse to.
  rc=0
  url=""; dir=""; out=""
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
      "  dir="*) dir="${line#  dir=}" ;;
      "  out="*) out="${line#  out=}"
                 mkdir -p "$dir"
                 curl -fL --retry 5 --retry-delay 5 --retry-connrefused \
                      --connect-timeout 30 -C - \
                      ${HF_TOKEN:+-H "Authorization: Bearer $HF_TOKEN"} \
                      -o "$dir/$out" "$url" || rc=1 ;;
      "") ;;
      *) url="$line" ;;
    esac
  done < "$CH_HOME/downloads.txt"
  return $rc
}

download_all > "$CH_HOME/download.log" 2>&1 &
DL_PID=$!
echo "[boot] downloads pid $DL_PID"

# Report real progress. "downloading flux1-dev-fp8.safetensors" told the user
# nothing about whether to wait another minute or another forty; bytes-on-disk
# against the catalog total does.
progress_watch() {
  while kill -0 "$DL_PID" 2>/dev/null; do
    got=$(du -sb "$COMFY/models" 2>/dev/null | cut -f1)
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

# --- 4. ComfyUI, while the weights land ------------------------------------
export DEBIAN_FRONTEND=noninteractive
apt-get update -y -o Acquire::Languages=none >/dev/null 2>&1 || true
apt-get install -y --no-install-recommends git ca-certificates ffmpeg >/dev/null 2>&1 || true
if ! command -v git >/dev/null 2>&1; then fail "git unavailable — cannot fetch ComfyUI"; exit 1; fi

if [ ! -d "$COMFY/.git" ]; then
  git clone --depth 1 https://github.com/comfyanonymous/ComfyUI "$COMFY" \
    || { fail "ComfyUI clone failed"; exit 1; }
fi
# --only-binary stops a stray source distribution from building against torch,
# which is the other way a CUDA stack gets quietly replaced.
"$PY" -m pip install --no-cache-dir --only-binary=:all: -r "$COMFY/requirements.txt" \
  || { fail "pip install of ComfyUI requirements failed"; exit 1; }

# --- 5. join the downloads -------------------------------------------------
wait "$DL_PID"; DL_RC=$?
kill "$WATCH_PID" 2>/dev/null || true
if [ "$DL_RC" -ne 0 ]; then
  fail "weight download failed (exit $DL_RC) — see download.log"
  exit 1
fi

# A gated repo or an expired link answers 200 with an HTML error page, which
# lands as a .safetensors and only fails hours later inside a render. Compare
# against the size the catalog measured and refuse anything short.
verify_one() {
  sub="$1"; name="$2"; want_gb="$3"
  dir="$COMFY/models/$sub"
  got=$(stat -c %s "$dir/$name" 2>/dev/null || echo 0)
  min=$(awk -v g="$want_gb" 'BEGIN{printf "%d", g*1073741824*0.9}')
  if [ "$got" -lt "$min" ]; then
    fail "$name is $got bytes, expected ~${want_gb}GB — the URL may be gated or moved"
    exit 1
  fi
  # ComfyUI has renamed these folders across versions and different nodes look
  # in different ones. Link rather than copy so it costs no disk.
  case "$sub" in
    diffusion_models) mkdir -p "$COMFY/models/unet" && ln -sf "$dir/$name" "$COMFY/models/unet/$name" ;;
    text_encoders)    mkdir -p "$COMFY/models/clip" && ln -sf "$dir/$name" "$COMFY/models/clip/$name" ;;
  esac
}

@@VERIFY@@

detail ""

# --- 5. run ----------------------------------------------------------------
stage starting
cd "$COMFY"
"$PY" main.py --listen 127.0.0.1 --port "$CHANTHRA_COMFY_PORT" >>"$CH_HOME/comfy.log" 2>&1 &
COMFY_PID=$!
echo "[boot] comfyui pid $COMFY_PID"

# Only report ready once ComfyUI actually answers. Saying "ready" when the
# process merely started would hand the studio a worker that 502s on submit.
for i in $(seq 1 180); do
  if curl -sf --max-time 5 "http://127.0.0.1:$CHANTHRA_COMFY_PORT/system_stats" >/dev/null 2>&1; then
    stage ready
    break
  fi
  if ! kill -0 "$COMFY_PID" 2>/dev/null; then
    fail "ComfyUI exited during startup — see comfy.log"
    exit 1
  fi
  sleep 5
done

if [ "$(cat "$CH_HOME/stage" 2>/dev/null)" != "ready" ]; then
  fail "ComfyUI did not answer within 15 minutes of starting"
  exit 1
fi

wait "$COMFY_PID"
fail "ComfyUI exited"
""";

    // =====================================================================
    // Auth proxy (Python 3 standard library only — no pip at boot time)
    // =====================================================================

    // Five-quote delimiter: the Python below contains its own triple-quoted
    // docstrings, which would otherwise close the literal early.
    private const string ProxySource = """""
# Chanthra Studio worker proxy.
#
# The only publicly reachable listener on this box. Checks a bearer token in
# constant time, then relays raw bytes to ComfyUI on loopback. Because it
# never parses bodies it transparently carries chunked responses, large file
# downloads, multipart uploads and the WebSocket upgrade for /ws.
import hmac
import json
import os
import select
import socket
import socketserver
import sys
import threading

TOKEN = os.environ.get("CHANTHRA_TOKEN", "").encode()
LISTEN_PORT = int(os.environ.get("CHANTHRA_PROXY_PORT", "20000"))
COMFY_PORT = int(os.environ.get("CHANTHRA_COMFY_PORT", "8188"))
PROFILE = os.environ.get("CHANTHRA_PROFILE", "")
HOME = "/opt/chanthra"
MAX_HEAD = 65536


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
            return None, buf
    head, rest = buf.split(b"\r\n\r\n", 1)
    return head, rest


def authorized(head):
    # An empty token would make compare_digest trivially satisfiable, so an
    # unconfigured worker refuses everything rather than opening up.
    if not TOKEN:
        return False
    expected = b"Bearer " + TOKEN
    for line in head.split(b"\r\n")[1:]:
        if line[:14].lower() == b"authorization:":
            supplied = line.split(b":", 1)[1].strip()
            # Constant-time: a plain == leaks the shared prefix length via
            # timing, which is enough to walk a token out one byte at a time.
            return hmac.compare_digest(supplied, expected)
    return False


def pump(a, b):
    """Relay bytes both ways until either side hangs up."""
    a.setblocking(True)
    b.setblocking(True)
    socks = [a, b]
    try:
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
    finally:
        for s in (a, b):
            try:
                s.close()
            except OSError:
                pass


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        client = self.request
        client.settimeout(120)

        head, rest = read_head(client)
        if head is None:
            return

        if not authorized(head):
            respond(client, 401, "Unauthorized", b'{"error":"bad or missing bearer token"}')
            return

        try:
            path = head.split(b"\r\n", 1)[0].split(b" ")[1]
        except IndexError:
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
            upstream.sendall(head + b"\r\n\r\n" + rest)
        except OSError:
            try:
                upstream.close()
            except OSError:
                pass
            return

        client.settimeout(None)
        pump(client, upstream)


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
