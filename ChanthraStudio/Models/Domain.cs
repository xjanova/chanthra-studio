using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

public enum ShotStatus { Queue, Generating, Done, Error }

public enum CamMode { Locked, Pan, Tilt, Push, Orbit, Dolly }

public enum AspectRatio { Vertical, Square, Wide, Cinema }

/// <summary>
/// Per-shot state. Inherits ObservableObject so progress / status / thumb
/// updates from the GenerationService propagate to the storyboard binding.
/// </summary>
public sealed class Shot : ObservableObject
{
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";

    private string _prompt = "";
    public string Prompt { get => _prompt; set => SetProperty(ref _prompt, value); }

    public string StyleId { get; set; } = "empress";
    public string ModelId { get; set; } = "chanthra-sora-lyra-2.4";
    public AspectRatio Aspect { get; set; } = AspectRatio.Wide;
    public double DurationSec { get; set; } = 8.0;
    public double Motion { get; set; } = 0.7;
    public (int A, int B) Seed { get; set; } = (2814, 9217);
    public bool Hd4k { get; set; } = true;
    public bool Audio { get; set; } = true;
    public CamMode Cam { get; set; } = CamMode.Locked;
    public ObservableCollection<string> Tags { get; } = new();

    private ShotStatus _status = ShotStatus.Queue;
    public ShotStatus Status { get => _status; set => SetProperty(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    private string? _thumbUrl;
    public string? ThumbUrl { get => _thumbUrl; set => SetProperty(ref _thumbUrl, value); }

    private string? _videoUrl;
    public string? VideoUrl { get => _videoUrl; set => SetProperty(ref _videoUrl, value); }

    private string _durationLabel = "8.0s";
    public string DurationLabel { get => _durationLabel; set => SetProperty(ref _durationLabel, value); }

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value); }
}

public sealed class Sequence
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public ObservableCollection<string> ShotIds { get; } = new();
}

public sealed class Project
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public ObservableCollection<string> SequenceIds { get; } = new();
    public string CurrentShotId { get; set; } = "";
}

public sealed class StylePreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ThumbPath { get; set; } = "";
}
