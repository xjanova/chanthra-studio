using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class GenerateViewModel : ObservableObject
{
    private readonly StudioContext? _ctx;

    private string _prompt = "ราชินีจันทรา on a throne of black silk and floating lotuses; ribbons of crimson smoke; gold halo splitting into eight beams; sloooow camera push, 24fps cinematic.";
    public string Prompt { get => _prompt; set => SetProperty(ref _prompt, value); }

    private string _sceneLabel = "Scene 03 · Shot 02";
    public string SceneLabel { get => _sceneLabel; set => SetProperty(ref _sceneLabel, value); }

    public ObservableCollection<WorkflowDescriptor> Workflows { get; } = new();

    private WorkflowDescriptor? _activeWorkflow;
    public WorkflowDescriptor? ActiveWorkflow
    {
        get => _activeWorkflow;
        set
        {
            if (!SetProperty(ref _activeWorkflow, value)) return;
            OnPropertyChanged(nameof(EngineLabel));
            OnPropertyChanged(nameof(EngineSpec));
            if (value is not null && _ctx is not null)
            {
                _ctx.Settings.ActiveWorkflow = value.Name;
                try { _ctx.Settings.Save(); } catch { /* non-fatal */ }
            }
        }
    }

    public IRelayCommand RefreshWorkflowsCommand { get; }

    public string EngineLabel => ActiveWorkflow?.DisplayName ?? "Chanthra · Sora-Lyra v2.4";
    public string EngineSpec => ActiveWorkflow?.Spec ?? "1080p · 24fps · text+image→video";

    private AspectRatio _aspect = AspectRatio.Wide;
    public AspectRatio Aspect { get => _aspect; set => SetProperty(ref _aspect, value); }

    private double _durationSec = 8.0;
    public double DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, value); }

    private double _motion = 0.7;
    public double Motion { get => _motion; set => SetProperty(ref _motion, value); }

    /// <summary>Display-only label combining the two seed components.</summary>
    public string SeedLabel => $"{SeedA} · {SeedB}";

    private bool _hd4k = true;
    public bool Hd4k { get => _hd4k; set => SetProperty(ref _hd4k, value); }

    private bool _nativeAudio = true;
    public bool NativeAudio { get => _nativeAudio; set => SetProperty(ref _nativeAudio, value); }

    private CamMode _camera = CamMode.Push;
    public CamMode Camera { get => _camera; set => SetProperty(ref _camera, value); }

    private string _activeStyleId = "empress";
    public string ActiveStyleId
    {
        get => _activeStyleId;
        set
        {
            if (!SetProperty(ref _activeStyleId, value)) return;
            // Sync IsActive flags on every preset so the picker's gold
            // highlight tracks the currently chosen style.
            foreach (var sp in StylePresets)
                sp.IsActive = sp.Id == value;
        }
    }

    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; set => SetProperty(ref _isGenerating, value); }

    private string _generatingDuration = "";
    public string GeneratingDuration { get => _generatingDuration; set => SetProperty(ref _generatingDuration, value); }

    /// <summary>Right side of the Summon CTA — was "−14 ☾" forever. Now blank
    /// until/unless the credit ledger feeds something in. Removed the fake
    /// counter rather than animate a meaningless number.</summary>
    private string _credits = "";
    public string Credits { get => _credits; set => SetProperty(ref _credits, value); }

    private string _breadcrumb = "Projects › The Empress";
    public string Breadcrumb { get => _breadcrumb; set => SetProperty(ref _breadcrumb, value); }

    /// <summary>
    /// The shot whose preview occupies the centre Stage. Defaults to the
    /// last shot the user generated (or clicked in the storyboard rail). All
    /// the SHOT/FRAME/LENS/SEED meta pills derive from this.
    /// </summary>
    private Shot? _activeShot;
    public Shot? ActiveShot
    {
        get => _activeShot;
        set
        {
            if (SetProperty(ref _activeShot, value))
            {
                OnPropertyChanged(nameof(StageImagePath));
                OnPropertyChanged(nameof(HasActiveShot));
                OnPropertyChanged(nameof(ShotMetaShot));
                OnPropertyChanged(nameof(ShotMetaFrame));
                OnPropertyChanged(nameof(ShotMetaLens));
                OnPropertyChanged(nameof(ShotMetaSeed));
            }
        }
    }

    public bool HasActiveShot => _activeShot is not null;

    /// <summary>Path the centre Stage <c>Image</c> displays — falls back to
    /// the brand poster when no shot has rendered yet.</summary>
    public string StageImagePath =>
        _activeShot?.VideoUrl ?? _activeShot?.ThumbUrl ?? "/Assets/Brand/empress-wide.png";

    public string ShotMetaShot => _activeShot is null ? "SHOT —" : $"SHOT {_activeShot.Number}";
    public string ShotMetaFrame => _activeShot is null
        ? "00:00:00:00"
        : $"00:00:{(int)System.Math.Floor(_activeShot.DurationSec):D2}:00";
    public string ShotMetaLens => _activeShot is null
        ? "—"
        : $"{_activeShot.Aspect.ToString().ToUpperInvariant()} · {_activeShot.Cam.ToString().ToUpperInvariant()}";
    public string ShotMetaSeed => _activeShot is null
        ? "SEED —"
        : $"SEED {_activeShot.Seed.A}·{_activeShot.Seed.B}";

    private string? _toastMessage;
    public string? ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }

    private string _toastKind = "info";
    public string ToastKind { get => _toastKind; set => SetProperty(ref _toastKind, value); }

    private string? _referenceImagePath;
    public string? ReferenceImagePath
    {
        get => _referenceImagePath;
        set
        {
            if (SetProperty(ref _referenceImagePath, value))
            {
                OnPropertyChanged(nameof(HasReferenceImage));
                OnPropertyChanged(nameof(ReferenceImageFileName));
            }
        }
    }

    public bool HasReferenceImage => !string.IsNullOrEmpty(_referenceImagePath);

    public string ReferenceImageFileName =>
        string.IsNullOrEmpty(_referenceImagePath)
            ? "drag image here · or click to browse"
            : System.IO.Path.GetFileName(_referenceImagePath);

    private string _negativePrompt = "blurry, low quality, watermark, text, deformed, extra fingers";
    public string NegativePrompt { get => _negativePrompt; set => SetProperty(ref _negativePrompt, value); }

    public IRelayCommand BrowseReferenceImageCommand { get; }
    public IRelayCommand ClearReferenceImageCommand { get; }
    public IRelayCommand<string> SetAspectCommand { get; }
    public IRelayCommand<string> SetCameraCommand { get; }
    public IRelayCommand<string> SetStyleCommand { get; }
    public IRelayCommand RegenerateSeedCommand { get; }
    public IRelayCommand<Shot> SelectShotCommand { get; }

    private int _seedA = 2814;
    public int SeedA
    {
        get => _seedA;
        set { if (SetProperty(ref _seedA, value)) OnPropertyChanged(nameof(SeedLabel)); }
    }

    private int _seedB = 9217;
    public int SeedB
    {
        get => _seedB;
        set { if (SetProperty(ref _seedB, value)) OnPropertyChanged(nameof(SeedLabel)); }
    }

    public ObservableCollection<StylePreset> StylePresets { get; } = new()
    {
        new() { Id = "empress",     Name = "Empress",      ThumbPath = "/Assets/Brand/empress-portrait.png" },
        new() { Id = "lunar-veil",  Name = "Lunar Veil",   ThumbPath = "/Assets/Brand/empress-tall-1.png" },
        new() { Id = "temple-silk", Name = "Temple Silk",  ThumbPath = "/Assets/Brand/empress-fullbody.png" },
        new() { Id = "oracle",      Name = "Oracle",       ThumbPath = "/Assets/Brand/empress-tall-2.png" },
    };

    public ObservableCollection<Shot> Storyboard { get; } = new();

    /// <summary>Count of shots not yet finished — drives the "Queue 3" badge.</summary>
    public int QueueCount => Storyboard.Count(s => s.Status == ShotStatus.Queue || s.Status == ShotStatus.Generating);

    public IAsyncRelayCommand SummonSceneCommand { get; }
    public IAsyncRelayCommand EnhancePromptCommand { get; }
    public IRelayCommand<Shot> PlayShotCommand { get; }
    public IRelayCommand<Shot> CancelShotCommand { get; }
    public IRelayCommand<Shot> RemoveShotCommand { get; }

    public GenerateViewModel() : this(null) { }

    public GenerateViewModel(StudioContext? ctx)
    {
        _ctx = ctx;
        SummonSceneCommand = new AsyncRelayCommand(SummonSceneAsync);
        EnhancePromptCommand = new AsyncRelayCommand(EnhancePromptAsync);
        RefreshWorkflowsCommand = new RelayCommand(LoadWorkflows);
        BrowseReferenceImageCommand = new RelayCommand(BrowseReferenceImage);
        ClearReferenceImageCommand = new RelayCommand(() => ReferenceImagePath = null);
        PlayShotCommand = new RelayCommand<Shot>(PlayShot);
        CancelShotCommand = new RelayCommand<Shot>(async s =>
        {
            if (s is null || _ctx is null) return;
            // We track the latest prompt id per shot via the Generation
            // service's internal map keyed by promptId. Here we cancel
            // every running job whose shotId matches — works for both
            // ComfyUI and Replicate routes.
            await _ctx.Generation.CancelByShotAsync(s.Id);
        });
        RemoveShotCommand = new RelayCommand<Shot>(s =>
        {
            if (s is not null)
            {
                Storyboard.Remove(s);
                if (ReferenceEquals(_activeShot, s)) ActiveShot = Storyboard.LastOrDefault();
            }
        });
        SelectShotCommand = new RelayCommand<Shot>(s => { if (s is not null) ActiveShot = s; });

        // Sync the initial style preset's IsActive flag so the picker
        // already shows a highlight at first paint.
        foreach (var sp in StylePresets) sp.IsActive = sp.Id == _activeStyleId;

        // QueueCount derives from Storyboard contents + each Shot.Status,
        // so re-publish it on both kinds of change.
        Storyboard.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (Shot s in e.NewItems) s.PropertyChanged += OnStoryboardShotPropertyChanged;
            if (e.OldItems is not null)
                foreach (Shot s in e.OldItems) s.PropertyChanged -= OnStoryboardShotPropertyChanged;
            OnPropertyChanged(nameof(QueueCount));
        };

        SetAspectCommand = new RelayCommand<string>(s =>
        {
            if (Enum.TryParse<AspectRatio>(s, true, out var a)) Aspect = a;
        });
        SetCameraCommand = new RelayCommand<string>(s =>
        {
            if (Enum.TryParse<CamMode>(s, true, out var c)) Camera = c;
        });
        SetStyleCommand = new RelayCommand<string>(s =>
        {
            if (!string.IsNullOrEmpty(s)) ActiveStyleId = s;
        });
        RegenerateSeedCommand = new RelayCommand(() =>
        {
            var rng = new Random();
            SeedA = rng.Next(1000, 9999);
            SeedB = rng.Next(1000, 9999);
        });

        if (_ctx is null)
        {
            // Design-time only — populate fake shots so the XAML designer
            // shows realistic content. At runtime _ctx is non-null and the
            // storyboard fills from real generations.
            SeedDesignTimeStoryboard();
            IsGenerating = false;
            return;
        }

        _ctx.Generation.ProgressChanged += OnGenerationProgress;
        LoadWorkflows();
        RebuildStoryboardFromHistory();
        IsGenerating = false;
    }

    /// <summary>
    /// Re-populate the right-rail Storyboard from the persisted <c>shots</c>
    /// table on launch. Phase 7.4 starts writing shots at submit time so
    /// rebuilds carry the full composer state (prompt, style, seed, camera)
    /// — not just thumbnails. Falls back to clip-grouping if the shots
    /// table is empty (e.g. legacy users from before 7.4).
    /// </summary>
    private void RebuildStoryboardFromHistory()
    {
        if (_ctx is null) return;
        try
        {
            var shots = _ctx.Shots.Recent(12);
            if (shots.Count > 0)
            {
                foreach (var s in shots) Storyboard.Add(s);
                ActiveShot ??= Storyboard.FirstOrDefault();
                return;
            }

            // Legacy fallback — pre-7.4 clip-only rebuild.
            var clips = _ctx.Clips.RecentClips(50);
            var byShot = clips
                .GroupBy(c => c.ShotId)
                .OrderByDescending(g => g.Max(c => c.CreatedAt))
                .Take(12)
                .ToList();

            int idx = 1;
            foreach (var group in byShot)
            {
                var primary = group.OrderByDescending(c => c.CreatedAt).First();
                Storyboard.Add(new Shot
                {
                    Id = group.Key,
                    Number = idx.ToString("D2"),
                    Title = string.IsNullOrEmpty(primary.FileName)
                        ? $"Shot {idx:D2}"
                        : System.IO.Path.GetFileNameWithoutExtension(primary.FileName),
                    Description = "Restored from clip history (pre-7.4 · prompt not preserved).",
                    Status = ShotStatus.Done,
                    ThumbUrl = primary.FilePath,
                    VideoUrl = primary.FilePath,
                    DurationLabel = primary.SizeLabel,
                });
                idx++;
            }
            ActiveShot ??= Storyboard.FirstOrDefault();
        }
        catch
        {
            // Rebuild is purely additive — silent failure means an empty
            // storyboard, which is the same as the pre-fix behaviour.
        }
    }

    private void SeedDesignTimeStoryboard()
    {
        Storyboard.Add(new Shot
        {
            Id = "01", Number = "01", Title = "Throne reveal",
            Description = "Slow tilt-up from black silk floor to the empress on her gold-threaded throne; halo flares.",
            DurationLabel = "2.4s", Status = ShotStatus.Done,
            ThumbUrl = "/Assets/Brand/empress-portrait.png",
            Tags = { "CINE", "WIDE", "NIGHT" },
        });
        Storyboard.Add(new Shot
        {
            Id = "02", Number = "02", Title = "Veil drop",
            Description = "Cascading mauve veil reveals her face; eyes catch a single shaft of crimson light.",
            DurationLabel = "1.8s", Status = ShotStatus.Done,
            ThumbUrl = "/Assets/Brand/empress-wide.png",
            Tags = { "CU", "DRAMA" },
        });
        Storyboard.Add(new Shot
        {
            Id = "03", Number = "03", Title = "Lotus ascent",
            Description = "Gold lotuses bloom in slow-motion around her shoulders, halo splits into eight beams.",
            DurationLabel = "3.1s", Status = ShotStatus.Generating, Progress = 62,
            ThumbUrl = "/Assets/Brand/empress-tall-1.png",
            Tags = { "MS", "GOLD", "BLOOM" },
        });
        Storyboard.Add(new Shot
        {
            Id = "04", Number = "04", Title = "Halo close",
            Description = "Camera pulls back as the eight beams converge into a single luminous ring above her crown.",
            DurationLabel = "2.0s", Status = ShotStatus.Queue,
            ThumbUrl = "/Assets/Brand/empress-tall-2.png",
            Tags = { "WIDE", "CLOSE" },
        });
        // Default Stage preview = the currently-rendering shot in the seed.
        ActiveShot = Storyboard.FirstOrDefault(s => s.Status == ShotStatus.Generating)
                  ?? Storyboard.LastOrDefault();
    }

    private void LoadWorkflows()
    {
        if (_ctx is null) return;
        _ctx.Workflows.Refresh();
        Workflows.Clear();
        foreach (var wf in _ctx.Workflows.All) Workflows.Add(wf);
        // Pick the persisted active, falling back to the first available.
        var match = Workflows.FirstOrDefault(w => w.Name == _ctx.Settings.ActiveWorkflow)
                  ?? Workflows.FirstOrDefault();
        if (!ReferenceEquals(match, _activeWorkflow))
        {
            _activeWorkflow = match;
            OnPropertyChanged(nameof(ActiveWorkflow));
            OnPropertyChanged(nameof(EngineLabel));
            OnPropertyChanged(nameof(EngineSpec));
        }
    }

    private async Task EnhancePromptAsync()
    {
        if (_ctx is null) return;
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            ShowToast("Type a few words first — the wand expands a draft.", "warn");
            return;
        }
        ShowToast("Enhancing prompt…", "info");
        try
        {
            var enhanced = await _ctx.Llm.EnhanceImagePromptAsync(Prompt);
            if (string.IsNullOrWhiteSpace(enhanced))
            {
                ShowToast("LLM returned empty — check your key + active provider.", "err");
                return;
            }
            Prompt = enhanced.Trim();
            ShowToast("Prompt enhanced ✓", "ok");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, "err");
        }
    }

    private async Task SummonSceneAsync()
    {
        if (_ctx is null)
        {
            ShowToast("Studio context not wired", "warn");
            return;
        }

        // Compose a new shot from the current composer state and append it to
        // the storyboard so the user sees the queued card immediately. The
        // seed comes from VM.SeedA/SeedB — user editable, regenerable via the
        // dice button — NOT a fresh random pair, so the displayed seed
        // matches what actually got submitted.
        var shot = new Shot
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            Number = (Storyboard.Count + 1).ToString("D2"),
            Title = $"Shot {Storyboard.Count + 1:D2}",
            Description = Prompt.Length > 140 ? Prompt[..140] + "…" : Prompt,
            Prompt = Prompt,
            StyleId = ActiveStyleId,
            Aspect = Aspect,
            DurationSec = DurationSec,
            Motion = Motion,
            Seed = (SeedA, SeedB),
            Hd4k = Hd4k,
            Audio = NativeAudio,
            Cam = Camera,
            Status = ShotStatus.Queue,
            DurationLabel = $"{DurationSec:F1}s",
            ThumbUrl = "/Assets/Brand/empress-portrait.png",
            ReferenceImagePath = ReferenceImagePath,
            NegativePrompt = NegativePrompt,
        };
        Storyboard.Add(shot);
        ActiveShot = shot;

        try
        {
            IsGenerating = true;
            _generationStartedAt = DateTimeOffset.UtcNow;
            StartGeneratingTimer();
            ShowToast("Submitting…", "info");
            var promptId = await _ctx.Generation.SubmitAsync(shot);
            shot.Status = ShotStatus.Generating;
            ShowToast($"Queued · {promptId[..8]}", "ok");
            // The card progress + final completion arrive via OnGenerationProgress.
        }
        catch (Exception ex)
        {
            shot.Status = ShotStatus.Error;
            ShowToast(ex.Message, "err");
            IsGenerating = false;
            StopGeneratingTimer();
        }
    }

    private DateTimeOffset? _generationStartedAt;
    private System.Windows.Threading.DispatcherTimer? _genTickTimer;

    /// <summary>Drives the "Generating · 14s" pill in the Stage bar by counting
    /// seconds since the most recent SubmitAsync started. Tick is 1Hz and
    /// auto-stops when nothing is generating anymore.</summary>
    private void StartGeneratingTimer()
    {
        if (_genTickTimer is not null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _genTickTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _genTickTimer.Tick += (_, _) =>
        {
            if (!IsGenerating || _generationStartedAt is null)
            {
                StopGeneratingTimer();
                return;
            }
            var elapsed = DateTimeOffset.UtcNow - _generationStartedAt.Value;
            GeneratingDuration = $"Generating · {(int)elapsed.TotalSeconds}s";
        };
        _genTickTimer.Start();
    }

    private void StopGeneratingTimer()
    {
        _genTickTimer?.Stop();
        _genTickTimer = null;
        GeneratingDuration = "";
    }

    private void OnStoryboardShotPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Shot.Status)) OnPropertyChanged(nameof(QueueCount));
    }

    private void OnGenerationProgress(object? sender, GenerationProgressEventArgs e)
    {
        var shot = Storyboard.FirstOrDefault(s => s.Id == e.ShotId);
        if (shot is null) return;
        shot.Status = e.Status;
        shot.Progress = e.Progress;
        if (e.MediaPath is not null)
        {
            shot.VideoUrl = e.MediaPath;
            shot.ThumbUrl = e.MediaPath;
            // Refresh the Stage if this is the currently-active shot — the
            // VideoUrl change alone doesn't republish StageImagePath because
            // it's a derived property on the VM, not on the Shot.
            if (ReferenceEquals(_activeShot, shot))
                OnPropertyChanged(nameof(StageImagePath));
        }

        if (e.Status == ShotStatus.Done)
        {
            var fileName = e.MediaPath is null ? "" : " · " + System.IO.Path.GetFileName(e.MediaPath);
            ShowToast($"Shot {shot.Number} ready{fileName}", "ok");
            IsGenerating = Storyboard.Any(s => s.Status == ShotStatus.Generating);
            if (!IsGenerating) StopGeneratingTimer();
            // Promote the just-finished shot to the active preview if nothing
            // is selected, or if the active was the same shot.
            if (_activeShot is null || ReferenceEquals(_activeShot, shot))
                ActiveShot = shot;
        }
        else if (e.Status == ShotStatus.Error)
        {
            ShowToast(e.Error ?? "generation failed", "err");
            IsGenerating = Storyboard.Any(s => s.Status == ShotStatus.Generating);
            if (!IsGenerating) StopGeneratingTimer();
        }
    }

    private void PlayShot(Shot? shot)
    {
        if (shot is null) return;
        var path = shot.VideoUrl ?? shot.ThumbUrl;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast(ex.Message, "err"); }
    }

    private void BrowseReferenceImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a reference image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            ReferenceImagePath = dlg.FileName;
    }

    private async void ShowToast(string message, string kind)
    {
        ToastMessage = message;
        ToastKind = kind;
        try
        {
            await Task.Delay(3500);
            if (ToastMessage == message) ToastMessage = null;
        }
        catch { }
    }
}
