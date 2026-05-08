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

    private string _negativePrompt = "blurry, low quality, watermark, text, deformed";
    public string NegativePrompt { get => _negativePrompt; set => SetProperty(ref _negativePrompt, value); }

    /// <summary>
    /// Local file path of an optional reference image. If set and the active
    /// ComfyUI workflow contains a LoadImage node, the file is uploaded to
    /// ComfyUI before submission and the LoadImage input is patched to point
    /// at the uploaded filename.
    /// </summary>
    private string? _referenceImagePath;
    public string? ReferenceImagePath { get => _referenceImagePath; set => SetProperty(ref _referenceImagePath, value); }

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

public sealed class StylePreset : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ThumbPath { get; set; } = "";

    private bool _isActive;
    /// <summary>True when the preset is the currently-selected style.
    /// Drives the gold border highlight in the picker grid.</summary>
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

/// <summary>Persisted clip — one row in the <c>clips</c> table.</summary>
public sealed class Clip : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; set; } = "";
    public string ShotId { get; set; } = "";
    public int DurationMs { get; set; }
    public string FilePath { get; set; } = "";
    public string? PosterPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    private bool _isSelected;
    /// <summary>UI-only — drives the multi-select checkbox in Library.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>How many <c>post_history</c> rows reference this clip — drives
    /// the small "Posted" badge on the Library card. Populated by the
    /// ClipsRepository's left-join query, NOT live data.</summary>
    private int _postCount;
    public int PostCount
    {
        get => _postCount;
        set
        {
            if (SetProperty(ref _postCount, value))
            {
                OnPropertyChanged(nameof(IsPosted));
                OnPropertyChanged(nameof(PostBadgeLabel));
            }
        }
    }

    public bool IsPosted => _postCount > 0;
    public string PostBadgeLabel => _postCount switch
    {
        0 => "",
        1 => "✓ Posted",
        _ => $"✓ Posted ×{_postCount}",
    };

    /// <summary>ISO date of the most recent post, "" if never posted.</summary>
    private string _lastPostedLabel = "";
    public string LastPostedLabel
    {
        get => _lastPostedLabel;
        set => SetProperty(ref _lastPostedLabel, value);
    }

    public string FileName => System.IO.Path.GetFileName(FilePath);
    public bool FileExists => System.IO.File.Exists(FilePath);

    public string SizeLabel
    {
        get
        {
            try
            {
                if (!FileExists) return "missing";
                var bytes = new System.IO.FileInfo(FilePath).Length;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
                return $"{bytes / 1024.0 / 1024.0:F1} MB";
            }
            catch { return "?"; }
        }
    }
}

/// <summary>
/// Descriptor for a ComfyUI workflow file the user can pick in the Composer.
/// Loaded lazily — we keep just the metadata in memory; the JSON itself is
/// re-read from disk on every generation so user edits land instantly.
/// </summary>
public sealed class WorkflowDescriptor
{
    public string Name { get; init; } = "";          // file name without .json
    public string DisplayName { get; init; } = "";   // human-friendly title
    public string Path { get; init; } = "";          // absolute disk path
    public string Description { get; init; } = "";   // first line of `// header` comment, if any
    public bool IsBuiltin { get; init; }             // shipped under bin/Assets/Workflows
    public string Spec { get; init; } = "";          // short uppercase mono caption (e.g. "SD1.5 · text→image")
}

/// <summary>Generation job row — submission record for any provider.</summary>
public sealed class GenerationJob
{
    public string Id { get; set; } = "";              // = ComfyUI prompt_id (or provider equivalent)
    public string ShotId { get; set; } = "";
    public string Provider { get; set; } = "comfyui";
    public string Status { get; set; } = "queued";    // queued | running | done | error | cancelled
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public string ShortId => Id.Length > 8 ? Id[..8] : Id;
    public TimeSpan? Duration => CompletedAt is { } end ? end - SubmittedAt : null;
    public string DurationLabel => Duration is { } d ? $"{d.TotalSeconds:F0}s" : "—";
}
