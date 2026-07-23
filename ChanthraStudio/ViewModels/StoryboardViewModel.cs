using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// The Storyboard studio. Pick a template, describe the concept + character,
/// let the active LLM expand it into a multi-clip board (each clip = title,
/// visual, camera, SFX, spoken Thai dialogue, tarot cards, audio behaviour),
/// then per clip: copy the assembled prompt, export a ready ComfyUI scene
/// workflow, or send it to the generation Queue on the engine of your choice
/// (Seedance / Veo / Kling / MiniMax / ComfyUI). A ready Facebook post ships
/// alongside the board.
///
/// Everything bound here is real — the Generate button calls the LLM, Send
/// drives <see cref="GenerationService"/>, Export writes a loadable workflow.
/// No fake counters, no stub actions (per the studio's "no UI lies" rule).
/// </summary>
public sealed class StoryboardViewModel : ObservableObject
{
    private readonly StudioContext? _ctx;

    public ObservableCollection<StoryboardTemplate> Templates { get; } = new(StoryboardTemplates.All);

    /// <summary>Implemented video engines, surfaced as the per-clip + default
    /// route picker. Mirrors the Composer's engine row.</summary>
    public ObservableCollection<VideoRouteOption> Routes { get; } = new();

    public ObservableCollection<PickOption> AspectOptions { get; } = new()
    {
        new() { Id = "9:16", Label = "9:16", Hint = "vertical · reels/tiktok" },
        new() { Id = "1:1", Label = "1:1", Hint = "square" },
        new() { Id = "16:9", Label = "16:9", Hint = "wide" },
        new() { Id = "21:9", Label = "21:9", Hint = "cinema" },
    };

    // ── Concept form ──────────────────────────────────────────────────────────
    private string _selectedTemplateId = "fortune-money";
    public string SelectedTemplateId { get => _selectedTemplateId; private set => SetProperty(ref _selectedTemplateId, value); }

    private string _concept = "";
    public string Concept { get => _concept; set => SetProperty(ref _concept, value); }

    private string _character = "";
    public string Character { get => _character; set { if (SetProperty(ref _character, value)) SyncSpecFromForm(); } }

    private string _styleNote = "realistic cinematic style";
    public string StyleNote { get => _styleNote; set { if (SetProperty(ref _styleNote, value)) SyncSpecFromForm(); } }

    private string _voiceNote = "sweet young Thai female voice age 18-22, cheerful";
    public string VoiceNote { get => _voiceNote; set { if (SetProperty(ref _voiceNote, value)) SyncSpecFromForm(); } }

    private string _referenceTag = "@ref1";
    public string ReferenceTag { get => _referenceTag; set { if (SetProperty(ref _referenceTag, value)) SyncSpecFromForm(); } }

    /// <summary>Push the live "locked" form fields into the shown board and
    /// rebuild every clip's video prompt, so Copy/Queue always use what the
    /// user sees — not the character the board was first generated with.</summary>
    private void SyncSpecFromForm()
    {
        if (_spec is null) return;
        _spec.Character = Character;
        _spec.StyleNote = StyleNote;
        _spec.VoiceNote = VoiceNote;
        _spec.ReferenceTag = ReferenceTag;
        StoryboardBuilder.RefreshVideoPrompts(_spec);
    }

    private int _clipCount = 4;
    public int ClipCount { get => _clipCount; set => SetProperty(ref _clipCount, Math.Clamp(value, 1, 8)); }

    private double _clipDurationSec = 8;
    public double ClipDurationSec
    {
        get => _clipDurationSec;
        set { if (SetProperty(ref _clipDurationSec, Math.Round(value))) OnPropertyChanged(nameof(ClipDurationLabel)); }
    }
    public string ClipDurationLabel => $"{ClipDurationSec:0}s";

    private string _aspectId = "9:16";
    public string AspectId
    {
        get => _aspectId;
        set
        {
            if (!SetProperty(ref _aspectId, value)) return;
            foreach (var a in AspectOptions) a.IsActive = a.Id == value;
            if (_spec is not null) { _spec.AspectId = value; StoryboardBuilder.RefreshVideoPrompts(_spec); }
        }
    }

    private string? _referenceImagePath;
    public string? ReferenceImagePath
    {
        get => _referenceImagePath;
        set
        {
            if (!SetProperty(ref _referenceImagePath, value)) return;
            OnPropertyChanged(nameof(HasReferenceImage));
            OnPropertyChanged(nameof(ReferenceImageLabel));
        }
    }
    public bool HasReferenceImage => !string.IsNullOrEmpty(_referenceImagePath);
    public string ReferenceImageLabel => string.IsNullOrEmpty(_referenceImagePath)
        ? "แนบภาพอ้างอิงตัวละคร · drag image or click"
        : Path.GetFileName(_referenceImagePath);

    private string? _sceneImagePath;
    public string? SceneImagePath
    {
        get => _sceneImagePath;
        set
        {
            if (!SetProperty(ref _sceneImagePath, value)) return;
            OnPropertyChanged(nameof(HasSceneImage));
            OnPropertyChanged(nameof(SceneImageLabel));
        }
    }
    public bool HasSceneImage => !string.IsNullOrEmpty(_sceneImagePath);
    public string SceneImageLabel => string.IsNullOrEmpty(_sceneImagePath)
        ? "แนบภาพฉาก / สถานที่ (ไม่บังคับ)"
        : Path.GetFileName(_sceneImagePath);

    private string? _outfitImagePath;
    public string? OutfitImagePath
    {
        get => _outfitImagePath;
        set
        {
            if (!SetProperty(ref _outfitImagePath, value)) return;
            OnPropertyChanged(nameof(HasOutfitImage));
            OnPropertyChanged(nameof(OutfitImageLabel));
        }
    }
    public bool HasOutfitImage => !string.IsNullOrEmpty(_outfitImagePath);
    public string OutfitImageLabel => string.IsNullOrEmpty(_outfitImagePath)
        ? "แนบภาพเสื้อผ้า / ชุด (ไม่บังคับ)"
        : Path.GetFileName(_outfitImagePath);

    private VideoRouteOption? _defaultRoute;
    /// <summary>Engine applied to every clip when a board is generated/loaded.
    /// Changing it re-stamps all clips (the user can still override per clip).</summary>
    public VideoRouteOption? DefaultRoute
    {
        get => _defaultRoute;
        set
        {
            var prev = _defaultRoute?.Id;
            if (!SetProperty(ref _defaultRoute, value)) return;
            if (value is null || _spec is null) return;
            // Re-stamp only clips the user hasn't individually overridden (those
            // still on the previous default), so per-clip choices survive — the
            // card tooltip promises per-clip override.
            foreach (var clip in _spec.Clips)
                if (string.IsNullOrEmpty(prev) || clip.Route == prev)
                    clip.Route = value.Id;
        }
    }

    // ── Board ─────────────────────────────────────────────────────────────────
    private StoryboardSpec? _spec;
    public StoryboardSpec? Spec
    {
        get => _spec;
        private set
        {
            if (!SetProperty(ref _spec, value)) return;
            OnPropertyChanged(nameof(Clips));
            OnPropertyChanged(nameof(HasSpec));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Facebook));
            OnPropertyChanged(nameof(BoardTitle));
        }
    }
    public bool HasSpec => _spec is not null && _spec.Clips.Count > 0;
    public bool IsEmpty => _spec is null || _spec.Clips.Count == 0;
    public ObservableCollection<StoryboardClip>? Clips => _spec?.Clips;
    public FacebookPost? Facebook => _spec?.Facebook;
    public string BoardTitle => _spec?.Title ?? "Storyboard";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) GenerateCommand.NotifyCanExecuteChanged(); }
    }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private string _statusKind = "info";
    public string StatusKind { get => _statusKind; set => SetProperty(ref _statusKind, value); }

    // ── Auto Pilot state ──────────────────────────────────────────────────────
    private System.Threading.CancellationTokenSource? _autoPilotCts;

    private bool _isAutoPilotRunning;
    public bool IsAutoPilotRunning
    {
        get => _isAutoPilotRunning;
        private set
        {
            if (!SetProperty(ref _isAutoPilotRunning, value)) return;
            OnPropertyChanged(nameof(IsAutoPilotIdle));
            AutoPilotCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsAutoPilotIdle => !_isAutoPilotRunning;

    private string _autoPilotStatus = "";
    public string AutoPilotStatus { get => _autoPilotStatus; private set => SetProperty(ref _autoPilotStatus, value); }

    private double _autoPilotPercent;
    public double AutoPilotPercent { get => _autoPilotPercent; private set => SetProperty(ref _autoPilotPercent, value); }

    /// <summary>Post the assembled film to the configured Facebook Page at the
    /// end of an Auto Pilot run. Persisted so the choice survives restarts.</summary>
    public bool AutoPostFacebook
    {
        get => _ctx?.Settings.StoryboardAutoPost ?? true;
        set
        {
            if (_ctx is null || _ctx.Settings.StoryboardAutoPost == value) return;
            _ctx.Settings.StoryboardAutoPost = value;
            try { _ctx.Settings.Save(); } catch { /* best-effort */ }
            OnPropertyChanged();
        }
    }

    public bool FacebookConfigured =>
        _ctx is not null
        && !string.IsNullOrWhiteSpace(_ctx.Settings.PostFacebookPageId)
        && !string.IsNullOrWhiteSpace(_ctx.Settings["facebook"]);

    public string FacebookTargetLabel => _ctx is null
        ? ""
        : FacebookConfigured
            ? $"Page {_ctx.Settings.PostFacebookPageId} · token ✓"
            : "ยังไม่ได้ตั้ง Page ID / token — Settings → Posting";

    // ── Commands ──────────────────────────────────────────────────────────────
    public IRelayCommand<string> SelectTemplateCommand { get; }
    public IRelayCommand<string> SetAspectCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IRelayCommand BrowseReferenceCommand { get; }
    public IRelayCommand ClearReferenceCommand { get; }
    public IRelayCommand BrowseSceneCommand { get; }
    public IRelayCommand ClearSceneCommand { get; }
    public IRelayCommand BrowseOutfitCommand { get; }
    public IRelayCommand ClearOutfitCommand { get; }
    public IAsyncRelayCommand AutoPilotCommand { get; }
    public IRelayCommand CancelAutoPilotCommand { get; }
    public IRelayCommand CopyBoardCommand { get; }
    public IRelayCommand CopyFacebookCommand { get; }
    public IRelayCommand<StoryboardClip> CopyClipPromptCommand { get; }
    public IRelayCommand<StoryboardClip> ExportClipComfyCommand { get; }
    public IRelayCommand ExportAllComfyCommand { get; }
    public IAsyncRelayCommand<StoryboardClip> SendClipCommand { get; }
    public IAsyncRelayCommand SendAllCommand { get; }
    public IRelayCommand<StoryboardClip> PlayClipCommand { get; }

    public StoryboardViewModel() : this(null) { }

    public StoryboardViewModel(StudioContext? ctx)
    {
        _ctx = ctx;

        SelectTemplateCommand = new RelayCommand<string>(SelectTemplate);
        SetAspectCommand = new RelayCommand<string>(id => { if (!string.IsNullOrEmpty(id)) AspectId = id; });
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy);
        BrowseReferenceCommand = new RelayCommand(() => BrowseInto(p => ReferenceImagePath = p, "เลือกภาพอ้างอิงตัวละคร (lock face/outfit)"));
        ClearReferenceCommand = new RelayCommand(() => ReferenceImagePath = null);
        BrowseSceneCommand = new RelayCommand(() => BrowseInto(p => SceneImagePath = p, "เลือกภาพฉาก / สถานที่"));
        ClearSceneCommand = new RelayCommand(() => SceneImagePath = null);
        BrowseOutfitCommand = new RelayCommand(() => BrowseInto(p => OutfitImagePath = p, "เลือกภาพเสื้อผ้า / ชุด"));
        ClearOutfitCommand = new RelayCommand(() => OutfitImagePath = null);
        AutoPilotCommand = new AsyncRelayCommand(RunAutoPilotAsync, () => !IsAutoPilotRunning);
        CancelAutoPilotCommand = new RelayCommand(() => _autoPilotCts?.Cancel());
        CopyBoardCommand = new RelayCommand(CopyBoard);
        CopyFacebookCommand = new RelayCommand(CopyFacebook);
        CopyClipPromptCommand = new RelayCommand<StoryboardClip>(CopyClipPrompt);
        ExportClipComfyCommand = new RelayCommand<StoryboardClip>(c => { if (c is not null) ExportClipComfy(c); });
        ExportAllComfyCommand = new RelayCommand(ExportAllComfy);
        SendClipCommand = new AsyncRelayCommand<StoryboardClip>(SendClipAsync);
        SendAllCommand = new AsyncRelayCommand(SendAllAsync);
        PlayClipCommand = new RelayCommand<StoryboardClip>(PlayClip);

        LoadRoutes();

        if (_ctx is not null)
            _ctx.Generation.ProgressChanged += OnGenerationProgress;

        // Land on template #1 with its ready-made example board already shown.
        SelectTemplate("fortune-money");
    }

    private void LoadRoutes()
    {
        Routes.Clear();
        if (_ctx is not null)
        {
            foreach (var p in _ctx.Providers.Video)
            {
                if (!p.IsImplemented) continue;
                Routes.Add(new VideoRouteOption { Id = p.Id, DisplayName = p.DisplayName });
            }
        }
        if (Routes.Count == 0)
        {
            // Design-time / no-ctx fallback so the picker isn't empty.
            Routes.Add(new VideoRouteOption { Id = "seedance", DisplayName = "Seedance 2.0 · ByteDance" });
            Routes.Add(new VideoRouteOption { Id = "veo", DisplayName = "Google Veo 3.1 (omni)" });
            Routes.Add(new VideoRouteOption { Id = "comfyui", DisplayName = "ComfyUI · local" });
        }
        // Prefer Seedance (native Thai dialogue) as the board default; else Veo; else first.
        _defaultRoute = Routes.FirstOrDefault(r => r.Id == "seedance")
                     ?? Routes.FirstOrDefault(r => r.Id == "veo")
                     ?? Routes.FirstOrDefault();
        OnPropertyChanged(nameof(DefaultRoute));
    }

    private void SelectTemplate(string? id)
    {
        var tpl = StoryboardTemplates.FindById(id ?? "") ?? StoryboardTemplates.All[0];
        SelectedTemplateId = tpl.Id;
        foreach (var t in Templates) t.IsActive = t.Id == tpl.Id;

        // Fill the form from the template.
        Concept = tpl.Concept;
        Character = tpl.Character;
        StyleNote = tpl.StyleNote;
        ReferenceTag = tpl.ReferenceTag;
        ClipCount = tpl.ClipCount;
        ClipDurationSec = tpl.ClipDurationSec;
        VoiceNote = tpl.VoiceNote;
        AspectId = tpl.AspectId;

        // If the template ships a ready board, show it immediately; otherwise
        // clear the canvas so the user generates from the concept.
        if (tpl.BuildExample is not null)
        {
            ApplyBoard(tpl.BuildExample());
            SetStatus("โหลดเทมเพลตสำเร็จ — แก้คอนเซ็ปต์แล้วกด Generate เพื่อสร้างใหม่ได้", "ok");
        }
        else
        {
            Spec = null;
            SetStatus("กรอกคอนเซ็ปต์แล้วกด ✦ Generate storyboard", "info");
        }
    }

    private async Task GenerateAsync()
    {
        if (_ctx is null) { SetStatus("Studio context not wired.", "warn"); return; }
        if (string.IsNullOrWhiteSpace(Concept) && string.IsNullOrWhiteSpace(Character))
        {
            SetStatus("พิมพ์คอนเซ็ปต์ก่อน (หรือเลือกเทมเพลต) แล้วค่อยกด Generate", "warn");
            return;
        }

        var srcTpl = StoryboardTemplates.FindById(SelectedTemplateId);
        var formTpl = new StoryboardTemplate
        {
            Id = SelectedTemplateId,
            // Seed the fallback title from the template, NOT the live board title,
            // so a regenerate doesn't inherit the previous board's AI-written title.
            Name = srcTpl?.Name ?? "Storyboard",
            Concept = Concept,
            Character = Character,
            StyleNote = StyleNote,
            ReferenceTag = ReferenceTag,
            AspectId = AspectId,
            ClipCount = ClipCount,
            ClipDurationSec = ClipDurationSec,
            VoiceNote = VoiceNote,
        };

        try
        {
            IsBusy = true;
            SetStatus("AI กำลังเขียนสตอรี่บอร์ด…", "info");
            var json = await _ctx.Llm.WriteStoryboardAsync(
                Concept, Character, StyleNote, ClipCount, ClipDurationSec, VoiceNote);
            if (string.IsNullOrWhiteSpace(json))
            {
                SetStatus("LLM ตอบกลับว่าง — เช็ค API key + provider ที่ตั้งไว้", "err");
                return;
            }
            var spec = StoryboardBuilder.Parse(json, formTpl);
            ApplyBoard(spec);
            SetStatus($"สร้างสตอรี่บอร์ดสำเร็จ · {spec.Clips.Count} คลิป ✓", "ok");
        }
        catch (Exception ex)
        {
            SetStatus("สร้างไม่สำเร็จ: " + ex.Message, "err");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adopt a parsed/loaded board: stamp the default route on each
    /// clip and (re)assemble its video prompt.</summary>
    private void ApplyBoard(StoryboardSpec spec)
    {
        // Stamp a route that actually exists in the picker, else the per-clip
        // ComboBox would render blank while Queue silently used a hidden id.
        var route = _defaultRoute?.Id
            ?? Routes.FirstOrDefault(r => r.Id == "seedance")?.Id
            ?? Routes.FirstOrDefault()?.Id
            ?? "seedance";
        foreach (var clip in spec.Clips) clip.Route = route;
        StoryboardBuilder.RefreshVideoPrompts(spec);
        Spec = spec;
    }

    private void CopyBoard()
    {
        if (_spec is null) { SetStatus("ยังไม่มีสตอรี่บอร์ดให้คัดลอก", "warn"); return; }
        if (TrySetClipboard(StoryboardBuilder.RenderBoardText(_spec)))
            SetStatus("คัดลอกสตอรี่บอร์ดทั้งหมดแล้ว ✓", "ok");
    }

    private void CopyFacebook()
    {
        if (_spec is null) { SetStatus("ยังไม่มีโพสต์ให้คัดลอก", "warn"); return; }
        if (TrySetClipboard(StoryboardBuilder.RenderFacebookText(_spec.Facebook)))
            SetStatus("คัดลอกคำโพสต์ Facebook แล้ว ✓", "ok");
    }

    private void CopyClipPrompt(StoryboardClip? clip)
    {
        if (clip is null) return;
        if (TrySetClipboard(clip.VideoPrompt))
            SetStatus($"คัดลอกพร้อมต์ CLIP {clip.Index} แล้ว ✓", "ok");
    }

    private void ExportClipComfy(StoryboardClip clip)
    {
        if (_spec is null) return;
        try
        {
            var graph = StoryboardBuilder.BuildComfyGraph(_spec, clip);
            var path = NodeFlowConverter.SaveToUserWorkflows(graph, $"storyboard-clip{clip.Index}");
            _ctx?.Workflows.Refresh();
            SetStatus($"บันทึก ComfyUI workflow → {Path.GetFileName(path)} (เลือกได้ใน Composer)", "ok");
        }
        catch (Exception ex) { SetStatus("Export ไม่สำเร็จ: " + ex.Message, "err"); }
    }

    private void ExportAllComfy()
    {
        if (_spec is null) { SetStatus("ยังไม่มีสตอรี่บอร์ด", "warn"); return; }
        try
        {
            var n = 0;
            foreach (var clip in _spec.Clips)
            {
                var graph = StoryboardBuilder.BuildComfyGraph(_spec, clip);
                NodeFlowConverter.SaveToUserWorkflows(graph, $"storyboard-clip{clip.Index}");
                n++;
            }
            _ctx?.Workflows.Refresh();
            SetStatus($"บันทึก {n} ComfyUI scene workflows แล้ว — เลือกได้ใน Composer ✓", "ok");
        }
        catch (Exception ex) { SetStatus("Export ไม่สำเร็จ: " + ex.Message, "err"); }
    }

    private async Task SendClipAsync(StoryboardClip? clip)
    {
        if (clip is null) return;
        if (_ctx is null) { SetStatus("Studio context not wired.", "warn"); return; }
        if (_spec is null) return;
        if (clip.IsBusy)
        {
            SetStatus($"CLIP {clip.Index} กำลังสร้างอยู่แล้ว", "warn");
            return;
        }

        var route = string.IsNullOrWhiteSpace(clip.Route) ? "seedance" : clip.Route;
        // Shared with Auto Pilot — one source of truth for what a clip submit
        // looks like (prompt assembly, refs, seed, aspect).
        var shot = StoryboardBuilder.BuildShot(_spec, clip, ReferenceImagePath, SceneImagePath, OutfitImagePath);

        // For the ComfyUI route, build + select a ready scene workflow so the
        // local pipeline actually has a graph to run (these 10 nodes render the
        // SCENE still — talking-head audio is a cloud-engine feature).
        string? workflowOverride = null;
        if (route == "comfyui")
        {
            try
            {
                var graph = StoryboardBuilder.BuildComfyGraph(_spec, clip);
                var path = NodeFlowConverter.SaveToUserWorkflows(graph, $"storyboard-clip{clip.Index}");
                _ctx.Workflows.Refresh();
                workflowOverride = Path.GetFileNameWithoutExtension(path);
            }
            catch { /* fall back to the active workflow if the export fails */ }
        }

        clip.ShotId = shot.Id;
        clip.Status = ShotStatus.Queue;
        clip.Progress = 0;
        try
        {
            await _ctx.Generation.SubmitAsync(shot, routeOverride: route, workflowOverride: workflowOverride);
            // A very fast engine can raise Done/Error via the progress event
            // before this line runs — only advance to Generating if still Queue
            // so we never clobber a finished status back to "generating".
            if (clip.Status == ShotStatus.Queue) clip.Status = ShotStatus.Generating;
            SetStatus($"ส่ง CLIP {clip.Index} เข้า Queue ({route}) ✓", "ok");
        }
        catch (Exception ex)
        {
            clip.Status = ShotStatus.Error;
            SetStatus($"CLIP {clip.Index}: {ex.Message}", "err");
        }
    }

    private async Task SendAllAsync()
    {
        if (_spec is null) { SetStatus("ยังไม่มีสตอรี่บอร์ด", "warn"); return; }
        var clips = _spec.Clips.ToList();
        var sent = 0;
        var failed = 0;
        foreach (var clip in clips)
        {
            if (clip.IsBusy) continue;
            await SendClipAsync(clip);
            // SendClipAsync flips a failed submit to Error; only count real sends.
            if (clip.Status == ShotStatus.Error) failed++;
            else sent++;
        }
        if (sent == 0 && failed == 0)
            SetStatus("ทุกคลิปกำลังสร้างอยู่แล้ว", "warn");
        else if (failed > 0)
            SetStatus($"ส่ง {sent} คลิป · ผิดพลาด {failed}", "warn");
        else
            SetStatus($"ส่ง {sent} คลิปเข้า Queue แล้ว ✓", "ok");
    }

    private void PlayClip(StoryboardClip? clip)
    {
        if (clip?.MediaPath is null || !File.Exists(clip.MediaPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(clip.MediaPath) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus(ex.Message, "err"); }
    }

    private void OnGenerationProgress(object? sender, GenerationProgressEventArgs e)
    {
        var clip = _spec?.Clips.FirstOrDefault(c => c.ShotId == e.ShotId);
        if (clip is null) return;
        clip.Status = e.Status;
        clip.Progress = e.Progress;
        if (e.MediaPath is not null) clip.MediaPath = e.MediaPath;

        if (e.Status == ShotStatus.Done)
            SetStatus($"CLIP {clip.Index} เสร็จแล้ว ✓", "ok");
        else if (e.Status == ShotStatus.Error)
            SetStatus($"CLIP {clip.Index}: {e.Error ?? "generation failed"}", "err");
    }

    private static void BrowseInto(Action<string> assign, string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) assign(dlg.FileName);
    }

    // ── Auto Pilot ────────────────────────────────────────────────────────────

    /// <summary>The whole pipeline on one button: render every clip, conform +
    /// concat into a single film, save to Library, and (optionally) publish to
    /// the configured Facebook Page with the board's caption + hashtags.</summary>
    private async Task RunAutoPilotAsync()
    {
        if (_ctx is null) { SetStatus("Studio context not wired.", "warn"); return; }
        if (_spec is null || _spec.Clips.Count == 0)
        {
            SetStatus("สร้างสตอรี่บอร์ดก่อน แล้วค่อยกด Auto Pilot", "warn");
            return;
        }
        if (AutoPostFacebook && !FacebookConfigured)
        {
            AutoPilotStatus = "ตั้ง Facebook Page ID + Page token ใน Settings → Posting ก่อน (หรือปิดสวิตช์โพสต์อัตโนมัติ)";
            SetStatus(AutoPilotStatus, "err");
            return;
        }

        _autoPilotCts = new System.Threading.CancellationTokenSource();
        IsAutoPilotRunning = true;
        AutoPilotPercent = 0;
        AutoPilotStatus = "เริ่ม Auto Pilot…";

        var progress = new Progress<StoryboardAutoPilot.AutoPilotProgress>(p =>
        {
            AutoPilotStatus = p.Message;
            AutoPilotPercent = p.Step switch
            {
                StoryboardAutoPilot.AutoPilotStep.Generating =>
                    p.TotalClips > 0 ? Math.Max(4, 80.0 * p.DoneClips / p.TotalClips) : 4,
                StoryboardAutoPilot.AutoPilotStep.Assembling => 85,
                StoryboardAutoPilot.AutoPilotStep.Saving => 92,
                StoryboardAutoPilot.AutoPilotStep.Posting => 96,
                StoryboardAutoPilot.AutoPilotStep.Done => 100,
                _ => AutoPilotPercent,
            };
        });

        try
        {
            var result = await _ctx.AutoPilot.RunAsync(
                _spec, ReferenceImagePath, SceneImagePath, OutfitImagePath,
                AutoPostFacebook, progress, _autoPilotCts.Token);

            if (result.Ok)
            {
                AutoPilotPercent = 100;
                AutoPilotStatus = result.FacebookPostId is null
                    ? $"เสร็จแล้ว ✓ ไฟล์รวมอยู่ใน Library · {Path.GetFileName(result.FinalPath)}"
                    : $"โพสต์ขึ้น Facebook แล้ว ✓ · post {result.FacebookPostId}";
                SetStatus(AutoPilotStatus, "ok");
            }
            else
            {
                AutoPilotStatus = result.Error ?? "Auto Pilot ผิดพลาด";
                SetStatus(AutoPilotStatus, "err");
            }
        }
        finally
        {
            IsAutoPilotRunning = false;
            _autoPilotCts.Dispose();
            _autoPilotCts = null;
        }
    }

    private static bool TrySetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text); return true; }
        catch { return false; }
    }

    private async void SetStatus(string message, string kind)
    {
        StatusMessage = message;
        StatusKind = kind;
        try
        {
            await Task.Delay(4000);
            if (StatusMessage == message) StatusMessage = null;
        }
        catch { }
    }
}
