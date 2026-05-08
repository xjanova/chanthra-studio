using System.Collections.ObjectModel;
using ChanthraStudio.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class GenerateViewModel : ObservableObject
{
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

    public IRelayCommand SummonSceneCommand { get; }

    public GenerateViewModel()
    {
        SummonSceneCommand = new RelayCommand(SummonScene);
    }

    private void SummonScene()
    {
        IsGenerating = true;
    }
}
