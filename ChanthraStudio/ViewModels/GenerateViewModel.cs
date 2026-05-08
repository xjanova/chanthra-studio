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

    private string _engineLabel = "Chanthra · Sora-Lyra v2.4";
    public string EngineLabel { get => _engineLabel; set => SetProperty(ref _engineLabel, value); }

    private string _engineSpec = "1080p · 24fps · text+image→video";
    public string EngineSpec { get => _engineSpec; set => SetProperty(ref _engineSpec, value); }

    private AspectRatio _aspect = AspectRatio.Wide;
    public AspectRatio Aspect { get => _aspect; set => SetProperty(ref _aspect, value); }

    private double _durationSec = 8.0;
    public double DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, value); }

    private double _motion = 0.7;
    public double Motion { get => _motion; set => SetProperty(ref _motion, value); }

    private string _seedLabel = "2814 · 9217";
    public string SeedLabel { get => _seedLabel; set => SetProperty(ref _seedLabel, value); }

    private bool _hd4k = true;
    public bool Hd4k { get => _hd4k; set => SetProperty(ref _hd4k, value); }

    private bool _nativeAudio = true;
    public bool NativeAudio { get => _nativeAudio; set => SetProperty(ref _nativeAudio, value); }

    private CamMode _camera = CamMode.Push;
    public CamMode Camera { get => _camera; set => SetProperty(ref _camera, value); }

    private string _activeStyleId = "empress";
    public string ActiveStyleId { get => _activeStyleId; set => SetProperty(ref _activeStyleId, value); }

    private bool _isGenerating = true;
    public bool IsGenerating { get => _isGenerating; set => SetProperty(ref _isGenerating, value); }

    private string _generatingDuration = "Generating · 14s";
    public string GeneratingDuration { get => _generatingDuration; set => SetProperty(ref _generatingDuration, value); }

    private string _credits = "−14 ☾";
    public string Credits { get => _credits; set => SetProperty(ref _credits, value); }

    private string _breadcrumb = "Projects › The Empress › Sequence 02 · Lotus Ascent";
    public string Breadcrumb { get => _breadcrumb; set => SetProperty(ref _breadcrumb, value); }

    private string _shotMetaShot = "SHOT 03·02";
    public string ShotMetaShot { get => _shotMetaShot; set => SetProperty(ref _shotMetaShot, value); }

    private string _shotMetaFrame = "00:08:14:23";
    public string ShotMetaFrame { get => _shotMetaFrame; set => SetProperty(ref _shotMetaFrame, value); }

    private string _shotMetaLens = "ANAMORPHIC 50MM · T1.8";
    public string ShotMetaLens { get => _shotMetaLens; set => SetProperty(ref _shotMetaLens, value); }

    private string _shotMetaSeed = "SEED 2814·9217";
    public string ShotMetaSeed { get => _shotMetaSeed; set => SetProperty(ref _shotMetaSeed, value); }

    private string? _toastMessage;
    public string? ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }

    private string _toastKind = "info";
    public string ToastKind { get => _toastKind; set => SetProperty(ref _toastKind, value); }

    public ObservableCollection<StylePreset> StylePresets { get; } = new()
    {
        new() { Id = "empress",     Name = "Empress",      ThumbPath = "/Assets/Brand/empress-portrait.png" },
        new() { Id = "lunar-veil",  Name = "Lunar Veil",   ThumbPath = "/Assets/Brand/empress-tall-1.png" },
        new() { Id = "temple-silk", Name = "Temple Silk",  ThumbPath = "/Assets/Brand/empress-fullbody.png" },
        new() { Id = "oracle",      Name = "Oracle",       ThumbPath = "/Assets/Brand/empress-tall-2.png" },
    };

    public ObservableCollection<Shot> Storyboard { get; } = new()
    {
        new()
        {
            Id = "01", Number = "01", Title = "Throne reveal",
            Description = "Slow tilt-up from black silk floor to the empress on her gold-threaded throne; halo flares.",
            DurationLabel = "2.4s", Status = ShotStatus.Done,
            ThumbUrl = "/Assets/Brand/empress-portrait.png",
            Tags = { "CINE", "WIDE", "NIGHT" },
        },
        new()
        {
            Id = "02", Number = "02", Title = "Veil drop",
            Description = "Cascading mauve veil reveals her face; eyes catch a single shaft of crimson light.",
            DurationLabel = "1.8s", Status = ShotStatus.Done,
            ThumbUrl = "/Assets/Brand/empress-wide.png",
            Tags = { "CU", "DRAMA" },
        },
        new()
        {
            Id = "03", Number = "03", Title = "Lotus ascent",
            Description = "Gold lotuses bloom in slow-motion around her shoulders, halo splits into eight beams.",
            DurationLabel = "3.1s", Status = ShotStatus.Generating, Progress = 62,
            ThumbUrl = "/Assets/Brand/empress-tall-1.png",
            Tags = { "MS", "GOLD", "BLOOM" },
        },
        new()
        {
            Id = "04", Number = "04", Title = "Halo close",
            Description = "Camera pulls back as the eight beams converge into a single luminous ring above her crown.",
            DurationLabel = "2.0s", Status = ShotStatus.Queue,
            ThumbUrl = "/Assets/Brand/empress-tall-2.png",
            Tags = { "WIDE", "CLOSE" },
        },
    };

    public IAsyncRelayCommand SummonSceneCommand { get; }

    public GenerateViewModel() : this(null) { }

    public GenerateViewModel(StudioContext? ctx)
    {
        _ctx = ctx;
        SummonSceneCommand = new AsyncRelayCommand(SummonSceneAsync);

        if (_ctx is not null)
            _ctx.Generation.ProgressChanged += OnGenerationProgress;
    }

    private async Task SummonSceneAsync()
    {
        if (_ctx is null)
        {
            ShowToast("Studio context not wired", "warn");
            return;
        }

        // Compose a new shot from the current composer state and append it to
        // the storyboard so the user sees the queued card immediately.
        var rng = new Random();
        var shot = new Shot
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            Number = (Storyboard.Count + 1).ToString("D2"),
            Title = $"Shot {Storyboard.Count + 1:D2}",
            Description = Prompt.Length > 140 ? Prompt[..140] + "…" : Prompt,
            Prompt = Prompt,
            Aspect = Aspect,
            DurationSec = DurationSec,
            Motion = Motion,
            Seed = (rng.Next(1000, 9999), rng.Next(1000, 9999)),
            Hd4k = Hd4k,
            Audio = NativeAudio,
            Cam = Camera,
            Status = ShotStatus.Queue,
            DurationLabel = $"{DurationSec:F1}s",
            ThumbUrl = "/Assets/Brand/empress-portrait.png",
        };
        Storyboard.Add(shot);

        try
        {
            IsGenerating = true;
            ShowToast("Submitting to ComfyUI…", "info");
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
        }
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
        }

        if (e.Status == ShotStatus.Done)
        {
            var fileName = e.MediaPath is null ? "" : " · " + System.IO.Path.GetFileName(e.MediaPath);
            ShowToast($"Shot {shot.Number} ready{fileName}", "ok");
            IsGenerating = Storyboard.Any(s => s.Status == ShotStatus.Generating);
        }
        else if (e.Status == ShotStatus.Error)
        {
            ShowToast(e.Error ?? "generation failed", "err");
            IsGenerating = Storyboard.Any(s => s.Status == ShotStatus.Generating);
        }
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
