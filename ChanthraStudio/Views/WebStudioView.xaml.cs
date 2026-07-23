using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace ChanthraStudio.Views;

/// <summary>
/// Hosts the embedded WebView2 for the Web Studio tab. The control owns the
/// browser (a visual); the <see cref="WebStudioViewModel"/> owns selection +
/// status and raises <see cref="WebNavIntent"/>s this code-behind executes.
///
/// The browser is initialised once with the selected site's persistent
/// environment so the login survives restarts. The view instance itself is
/// reused across tab switches (it is the MainWindow ViewSwitcher's cached
/// content), so the browser process and its login session stay alive for the
/// app lifetime; <see cref="OnUnloaded"/> only detaches the VM event to avoid
/// a subscriber leak, and re-attaches on the next <see cref="OnLoaded"/>.
/// </summary>
public partial class WebStudioView : UserControl
{
    private WebStudioViewModel? _vm;
    private bool _initStarted;
    private string? _pendingUrl;
    private bool _running;
    private CancellationTokenSource? _runCts;

    public WebStudioView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The ViewSwitcher leaves DataContext inheriting MainViewModel — the
        // explicit `DataContext="{Binding WebStudio}"` in the Setter does NOT
        // reliably apply (same gotcha VoiceView/UsageView document), so the
        // bindings + InitBrowserAsync never see a real VM. Resolve our own VM
        // from the studio here. The view instance is cached + reused, so this
        // VM persists and is the one the toolbar binds to.
        if (DataContext is not WebStudioViewModel vm)
        {
            var s = App.Current?.Studio;
            if (s is null) return;
            vm = new WebStudioViewModel(s);
            DataContext = vm;
        }

        _vm = vm;
        _vm.NavIntent -= OnNavIntent; // idempotent re-subscribe (Unloaded detaches)
        _vm.NavIntent += OnNavIntent;
        _vm.TeachModeChanged -= OnTeachModeChanged;
        _vm.TeachModeChanged += OnTeachModeChanged;
        _vm.RunRequested -= OnRunRequested;
        _vm.RunRequested += OnRunRequested;
        _vm.CancelRequested -= OnCancelRequested;
        _vm.CancelRequested += OnCancelRequested;

        if (_initStarted) return;
        _initStarted = true;
        await InitBrowserAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NavIntent -= OnNavIntent;
            _vm.TeachModeChanged -= OnTeachModeChanged;
            _vm.RunRequested -= OnRunRequested;
            _vm.CancelRequested -= OnCancelRequested;
        }
        // Leaving the tab must stop an in-flight run — the browser keeps living
        // (cached view), so a run would otherwise keep driving the page unseen.
        _runCts?.Cancel();
    }

    private void OnCancelRequested() => _runCts?.Cancel();

    private async Task InitBrowserAsync()
    {
        if (_vm?.Service is null) return;

        var site = _vm.SelectedSite;
        var startUrl = _vm.SelectedTool?.Url ?? site?.HomeUrl;
        try
        {
            // Distinguish "runtime not installed" from every other failure, so a
            // user who HAS the runtime isn't told to go install it.
            string? version = null;
            try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { version = null; }
            if (string.IsNullOrEmpty(version))
            {
                _vm.StatusText = "ไม่พบ Microsoft Edge WebView2 Runtime — ต้องติดตั้งก่อนใช้ Web Studio (ปกติมากับ Windows 11)";
                return;
            }

            _vm.StatusText = "กำลังเริ่มเบราว์เซอร์…";
            var env = await _vm.Service.GetEnvironmentAsync(site?.Id ?? "default");
            await Web.EnsureCoreWebView2Async(env);
            if (Web.CoreWebView2 is null) return; // unloaded mid-await

            Web.CoreWebView2.DownloadStarting += OnDownloadStarting;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.NavigationStarting += (_, args) =>
            {
                if (_vm is null) return;
                _vm.IsBusy = true;
                _vm.StatusText = "กำลังโหลด… " + WebStudioViewModel.RedactUrl(args.Uri);
            };
            Web.NavigationCompleted += (_, args) =>
            {
                if (_vm is null) return;
                _vm.IsBusy = false;
                _vm.StatusText = args.IsSuccess
                    ? WebStudioViewModel.RedactUrl(Web.Source?.ToString())
                    : $"โหลดไม่สำเร็จ ({args.WebErrorStatus})";
                // A page load wipes injected globals — re-arm teach mode if on.
                _ = ReinjectTeachIfOnAsync();
            };
            Web.SourceChanged += (_, __) =>
            {
                if (_vm is not null && Web.Source is not null)
                    _vm.OnBrowserUrlChanged(Web.Source.ToString());
            };

            _vm.IsBrowserReady = true;

            // Honour a navigation requested during init instead of dropping it.
            var target = _pendingUrl ?? startUrl;
            _pendingUrl = null;
            if (!string.IsNullOrWhiteSpace(target) && Uri.TryCreate(target, UriKind.Absolute, out var uri))
                Web.Source = uri;
        }
        catch (Exception ex)
        {
            _vm.StatusText = "เปิดเบราว์เซอร์ไม่สำเร็จ — ลองใหม่อีกครั้ง";
            TryLog(ex);
        }
    }

    private void OnNavIntent(WebNavIntent intent)
    {
        if (Web.CoreWebView2 is null)
        {
            // Browser still initialising — remember a Go target and apply it
            // once init completes, rather than silently dropping it.
            if (intent.Kind == WebNavKind.Go && !string.IsNullOrWhiteSpace(intent.Url))
                _pendingUrl = intent.Url;
            return;
        }

        switch (intent.Kind)
        {
            case WebNavKind.Go:
                if (!string.IsNullOrWhiteSpace(intent.Url) &&
                    Uri.TryCreate(intent.Url, UriKind.Absolute, out var uri))
                    Web.Source = uri;
                break;
            case WebNavKind.Back:
                if (Web.CanGoBack) Web.GoBack();
                break;
            case WebNavKind.Forward:
                if (Web.CanGoForward) Web.GoForward();
                break;
            case WebNavKind.Reload:
                Web.Reload();
                break;
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var folder = _vm?.Service?.DownloadsFolder;
            if (string.IsNullOrEmpty(folder)) return;

            var name = Path.GetFileName(e.DownloadOperation.ResultFilePath);
            if (string.IsNullOrWhiteSpace(name)) name = "download";
            e.ResultFilePath = UniquePath(folder, name);

            var op = e.DownloadOperation;
            EventHandler<object>? handler = null;
            handler = (_, __) =>
            {
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        if (_vm is not null) _vm.StatusText = "ดาวน์โหลดแล้ว: " + op.ResultFilePath;
                        op.StateChanged -= handler;
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        if (_vm is not null) _vm.StatusText = "ดาวน์โหลดถูกขัดจังหวะ";
                        op.StateChanged -= handler;
                        break;
                }
            };
            op.StateChanged += handler;
        }
        catch { /* download redirect is best-effort */ }
    }

    /// <summary>Avoid silently overwriting an earlier download with the same
    /// name (a generator often names every output identically).</summary>
    private static string UniquePath(string folder, string name)
    {
        var dest = Path.Combine(folder, name);
        if (!File.Exists(dest)) return dest;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return dest;
    }

    private static void TryLog(Exception ex)
    {
        // Log details to file — never surface raw exception text in the UI.
        try
        {
            var path = Path.Combine(AppPaths.LogsFolder, "webstudio.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch { }
    }

    // ── teach mode ─────────────────────────────────────────────────────────────

    private async void OnTeachModeChanged(bool on)
    {
        if (Web.CoreWebView2 is null) return;
        try
        {
            await Web.CoreWebView2.ExecuteScriptAsync(WebTeachScript.Define);
            await Web.CoreWebView2.ExecuteScriptAsync(on ? WebTeachScript.Enable : WebTeachScript.Disable);
        }
        catch { }
    }

    private async Task ReinjectTeachIfOnAsync()
    {
        if (_vm?.TeachMode != true || Web.CoreWebView2 is null) return;
        try
        {
            await Web.CoreWebView2.ExecuteScriptAsync(WebTeachScript.Define);
            await Web.CoreWebView2.ExecuteScriptAsync(WebTeachScript.Enable);
        }
        catch { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The page is untrusted data: only honour captures while teach mode is
        // actually on (the JS `on` flag can be bypassed by any page calling
        // postMessage directly), and only from the top-level page's own origin
        // so a cross-origin iframe/ad script can't forge steps.
        if (_vm?.TeachMode != true) return;
        if (!SameHost(e.Source, Web.Source?.ToString())) return;

        // Runs on the UI thread — safe to touch the VM's ObservableCollection.
        try
        {
            string json;
            try { json = e.TryGetWebMessageAsString(); }
            catch { json = e.WebMessageAsJson; }
            if (string.IsNullOrEmpty(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (GetStr(root, "type") != "capture") return;

            var kind = GetStr(root, "kind");
            _vm?.AddCapturedStep(
                GetStr(root, "css"),
                GetStr(root, "text"),
                string.IsNullOrEmpty(kind) ? "Click" : kind,
                GetStr(root, "label"));
        }
        catch { /* ignore malformed teach messages */ }
    }

    private static string GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>True when both URLs parse and share the same host — used to
    /// reject teach messages from cross-origin frames.</summary>
    private static bool SameHost(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) &&
        Uri.TryCreate(b, UriKind.Absolute, out var ub) &&
        string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase);

    // ── recipe runner ───────────────────────────────────────────────────────────

    private async void OnRunRequested()
    {
        if (_running || _vm is null || Web.CoreWebView2 is null) return;
        var recipe = _vm.CurrentRecipe();
        if (recipe is null)
        {
            _vm.StatusText = "ยังไม่มี recipe สำหรับเครื่องมือนี้ — สอนก่อน";
            return;
        }
        if (string.IsNullOrWhiteSpace(_vm.RunPrompt))
        {
            _vm.StatusText = "ใส่ prompt ก่อนรัน";
            return;
        }

        _running = true;
        _vm.IsRunning = true;
        _runCts = new CancellationTokenSource();
        try
        {
            await RunRecipeAsync(recipe, _vm.BuildRunInputs(), _runCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (_vm is not null) _vm.StatusText = "ยกเลิกแล้ว";
        }
        catch (Exception ex)
        {
            if (_vm is not null) _vm.StatusText = "รัน recipe ไม่สำเร็จ";
            TryLog(ex);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            _running = false;
            if (_vm is not null) _vm.IsRunning = false;
        }
    }

    private async Task RunRecipeAsync(WebRecipe recipe, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        if (Web.CoreWebView2 is null || _vm is null) return;
        await Web.CoreWebView2.ExecuteScriptAsync(WebRunScript.Define);

        var skippedUntaught = false;

        foreach (var step in recipe.Steps)
        {
            ct.ThrowIfCancellationRequested();

            var css = step.Css ?? "";
            if (string.IsNullOrWhiteSpace(css) || css == "TEACH_ME")
            {
                // Un-taught step (e.g. result/download not captured yet) — skip
                // rather than abort the whole run.
                skippedUntaught = true;
                _vm.StatusText = $"ข้าม '{step.Label}' (ยังไม่ได้สอน selector)";
                continue;
            }

            // Upload ALWAYS uses the file the user picked this run — never a path
            // embedded in the (page-authored, untrusted) recipe — so a poisoned
            // recipe cannot exfiltrate an arbitrary local file.
            var value = step.Kind == RecipeStepKinds.Upload
                ? _vm.RunImagePath ?? ""
                : Substitute(step.Value, inputs);

            // A no-value Upload/SelectModel is a no-op (keep current), not a failure.
            if ((step.Kind == RecipeStepKinds.Upload || step.Kind == RecipeStepKinds.SelectModel
                 || step.Kind == RecipeStepKinds.SelectOption) && string.IsNullOrWhiteSpace(value))
            {
                if (step.Kind != RecipeStepKinds.Upload)
                    _vm.StatusText = $"ข้าม '{step.Label}' — ใช้ค่าปัจจุบันบนหน้าเว็บ";
                continue;
            }

            _vm.StatusText = $"กำลังทำ: {step.Label}";

            var ok = step.Kind switch
            {
                RecipeStepKinds.Fill => await CallRunAsync("fill", new { css, text = step.Text, value }),
                RecipeStepKinds.Click => await ClickStepAsync(step, css, ct),
                RecipeStepKinds.Download => await ClickStepAsync(step, css, ct), // DownloadStarting routes the file
                RecipeStepKinds.SelectModel or RecipeStepKinds.SelectOption => await SelectStepAsync(step, css, value, ct),
                RecipeStepKinds.Upload => await UploadStepAsync(css, value),
                RecipeStepKinds.WaitFor => await WaitForAsync(css, step.Text, step.WaitMs ?? 120_000, ct),
                _ => true,
            };

            if (!ok && !step.Optional)
            {
                _vm.StatusText = $"หยุด: '{step.Label}' ไม่สำเร็จ";
                return;
            }
            await Task.Delay(450, ct); // let the page settle between steps
        }

        _vm.StatusText = skippedUntaught
            ? "รันถึงขั้นที่สอนไว้แล้ว — ขั้นที่ยังไม่ได้สอน (เช่น รอผล/ดาวน์โหลด) ถูกข้าม"
            : "รัน recipe เสร็จ — ผลที่ดาวน์โหลดอยู่ในโฟลเดอร์ WebDownloads";
    }

    /// <summary>Single-pass placeholder expansion — substituted text is never
    /// re-scanned, so a value inserted by one key (e.g. a local image path)
    /// can't be re-expanded by another, and a user prompt containing a literal
    /// "{{image}}" stays literal.</summary>
    private static string Substitute(string? template, IReadOnlyDictionary<string, string> inputs)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return Regex.Replace(template, @"\{\{(\w+)\}\}",
            m => inputs.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private async Task<bool> CallRunAsync(string fn, object args)
    {
        if (Web.CoreWebView2 is null) return false;
        // css/value travel as a JSON literal argument — never concatenated into code.
        var json = JsonSerializer.Serialize(args);
        var raw = await Web.CoreWebView2.ExecuteScriptAsync($"window.__cstudioRun.{fn}({json})");
        return ParseBool(raw, "ok");
    }

    private async Task<bool> ClickStepAsync(RecipeStep step, string css, CancellationToken ct)
    {
        if (step.WaitForEnabled)
        {
            var until = Environment.TickCount64 + (step.WaitMs ?? 60_000);
            while (Environment.TickCount64 < until)
            {
                if (await QueryEnabledAsync(css, step.Text)) break;
                await Task.Delay(500, ct);
            }
        }
        return await CallRunAsync("click", new { css, text = step.Text, requireEnabled = step.WaitForEnabled });
    }

    private async Task<bool> SelectStepAsync(RecipeStep step, string css, string value, CancellationToken ct)
    {
        if (!await CallRunAsync("openSelect", new { css, text = step.Text })) return false;
        await Task.Delay(600, ct); // wait for the dialog/listbox to render
        return await CallRunAsync("pick", new { value });
    }

    private async Task<bool> UploadStepAsync(string css, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!File.Exists(value))
        {
            if (_vm is not null) _vm.StatusText = "ไม่พบไฟล์ภาพ: " + value;
            return false;
        }
        return await SetFileInputAsync(css, value);
    }

    /// <summary>Set a hidden file input via CDP (never click it — that opens a
    /// native picker we can't drive). The selector goes through CDP as a JSON
    /// param, not injected JS.</summary>
    private async Task<bool> SetFileInputAsync(string css, string filePath)
    {
        if (Web.CoreWebView2 is null) return false;
        try
        {
            var doc = await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0}");
            var rootId = JsonDocument.Parse(doc).RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();

            var qp = JsonSerializer.Serialize(new { nodeId = rootId, selector = css });
            var q = await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.querySelector", qp);
            var nodeId = JsonDocument.Parse(q).RootElement.GetProperty("nodeId").GetInt32();
            if (nodeId == 0) return false;

            var sp = JsonSerializer.Serialize(new { nodeId, files = new[] { filePath } });
            await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", sp);
            return true;
        }
        catch (Exception ex)
        {
            TryLog(ex);
            return false;
        }
    }

    private async Task<bool> WaitForAsync(string css, string? text, int waitMs, CancellationToken ct)
    {
        var until = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < until)
        {
            if (await QueryPresentAsync(css, text)) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    private async Task<bool> QueryPresentAsync(string css, string? text)
    {
        if (Web.CoreWebView2 is null) return false;
        var json = JsonSerializer.Serialize(new { css, text });
        var raw = await Web.CoreWebView2.ExecuteScriptAsync($"window.__cstudioRun.query({json})");
        return ParseBool(raw, "ok");
    }

    private async Task<bool> QueryEnabledAsync(string css, string? text)
    {
        if (Web.CoreWebView2 is null) return false;
        var json = JsonSerializer.Serialize(new { css, text });
        var raw = await Web.CoreWebView2.ExecuteScriptAsync($"window.__cstudioRun.query({json})");
        return ParseBool(raw, "enabled");
    }

    private static bool ParseBool(string? raw, string prop)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return false;
        try
        {
            using var d = JsonDocument.Parse(raw);
            return d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty(prop, out var v)
                && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
}
