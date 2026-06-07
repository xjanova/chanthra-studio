using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

/// <summary>
/// One spoken line in a storyboard clip. <see cref="Emotion"/> is an optional
/// delivery cue ("cheerful", "calm, mysterious") the video model can read.
/// </summary>
public sealed class DialogueLine : ObservableObject
{
    private string _text = "";
    public string Text { get => _text; set => SetProperty(ref _text, value); }

    private string _emotion = "";
    public string Emotion { get => _emotion; set => SetProperty(ref _emotion, value); }

    public DialogueLine() { }
    public DialogueLine(string text, string emotion = "") { _text = text; _emotion = emotion; }
}

/// <summary>
/// One 4–10s beat of a storyboard. Carries everything a talking-head video
/// model needs (visual, camera language, SFX, spoken lines, props/cards) plus
/// the per-clip generation <see cref="Route"/> and a live status mirror so the
/// card animates while the engine renders it.
/// </summary>
public sealed class StoryboardClip : ObservableObject
{
    public int Index { get; set; } = 1;

    private string _title = "";
    public string Title { get => _title; set { if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(Header)); } }

    private double _durationSec = 8;
    public double DurationSec
    {
        get => _durationSec;
        set { if (SetProperty(ref _durationSec, value)) { OnPropertyChanged(nameof(DurationLabel)); OnPropertyChanged(nameof(Header)); } }
    }

    private string _visual = "";
    public string Visual { get => _visual; set => SetProperty(ref _visual, value); }

    private string _camera = "";
    public string Camera { get => _camera; set => SetProperty(ref _camera, value); }

    private string _sfx = "";
    public string Sfx { get => _sfx; set => SetProperty(ref _sfx, value); }

    private string _audioBehavior = "";
    public string AudioBehavior { get => _audioBehavior; set => SetProperty(ref _audioBehavior, value); }

    public ObservableCollection<DialogueLine> Dialogue { get; } = new();
    public ObservableCollection<string> Cards { get; } = new();

    /// <summary>The assembled, ready-to-paste video prompt (character + scene +
    /// quoted dialogue + audio behaviour). Rebuilt by
    /// <see cref="Services.StoryboardBuilder"/> whenever the board or the
    /// character changes — this is the string actually sent to the engine.</summary>
    private string _videoPrompt = "";
    public string VideoPrompt { get => _videoPrompt; set => SetProperty(ref _videoPrompt, value); }

    /// <summary>Per-clip engine route id (seedance / veo / kling / minimax /
    /// comfyui). Defaults to the board's chosen engine; the user can override
    /// it per clip in the card's dropdown.</summary>
    private string _route = "seedance";
    public string Route { get => _route; set => SetProperty(ref _route, value); }

    // ── live generation mirror ───────────────────────────────────────────────
    /// <summary>Set when the clip is submitted — used to match progress events
    /// back to this card, and to tell a never-sent clip ("READY") apart from an
    /// in-flight one (the default <see cref="ShotStatus.Queue"/> means both).</summary>
    private string _shotId = "";
    public string ShotId
    {
        get => _shotId;
        set
        {
            if (!SetProperty(ref _shotId, value)) return;
            OnPropertyChanged(nameof(HasBeenSent));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    private ShotStatus _status = ShotStatus.Queue;
    public ShotStatus Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsDone));
        }
    }

    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    private string? _mediaPath;
    public string? MediaPath { get => _mediaPath; set => SetProperty(ref _mediaPath, value); }

    /// <summary>True once the clip has actually been submitted for generation.</summary>
    public bool HasBeenSent => !string.IsNullOrEmpty(_shotId);

    /// <summary>In-flight = submitted AND not yet finished. A fresh, never-sent
    /// clip is NOT busy even though its default status is Queue — so its card
    /// shows no progress bar and the Queue button stays enabled.</summary>
    public bool IsBusy => HasBeenSent && _status is ShotStatus.Queue or ShotStatus.Generating;
    public bool IsDone => _status == ShotStatus.Done;

    public string DurationLabel => $"{DurationSec:0}s";
    public string Header => $"CLIP {Index} — {DurationLabel} · {Title}";

    public string StatusLabel => _status switch
    {
        ShotStatus.Generating => "GENERATING",
        ShotStatus.Done => "DONE",
        ShotStatus.Error => "ERROR",
        _ => HasBeenSent ? "QUEUED" : "READY",
    };
}

/// <summary>The ready-to-publish Facebook post that ships with a board:
/// caption + hashtag block + comma-separated tag block.</summary>
public sealed class FacebookPost : ObservableObject
{
    private string _caption = "";
    public string Caption { get => _caption; set => SetProperty(ref _caption, value); }

    public ObservableCollection<string> Hashtags { get; } = new();
    public ObservableCollection<string> CommaTags { get; } = new();

    public string HashtagLine => string.Join(" ", Hashtags);
    public string CommaTagLine => string.Join(", ", CommaTags);

    /// <summary>Republish the derived label strings after the collections are
    /// repopulated (ObservableCollection item adds don't fire on the host).</summary>
    public void NotifyDerived()
    {
        OnPropertyChanged(nameof(HashtagLine));
        OnPropertyChanged(nameof(CommaTagLine));
    }
}

/// <summary>
/// A full storyboard — concept hook + character + N clips + a ready Facebook
/// caption. The Storyboard view renders <see cref="Clips"/> in the centre and
/// <see cref="Facebook"/> on the right.
/// </summary>
public sealed class StoryboardSpec : ObservableObject
{
    private string _title = "";
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    /// <summary>The opening hook / pitch (the long spoken intro).</summary>
    public string Concept { get; set; } = "";

    /// <summary>Locked character description carried into every clip prompt so
    /// the face/outfit stays consistent across the board.</summary>
    public string Character { get; set; } = "";
    public string StyleNote { get; set; } = "realistic cinematic style";
    public string ReferenceTag { get; set; } = "@ref1";
    public string AspectId { get; set; } = "9:16";
    public string VoiceNote { get; set; } = "sweet young Thai female voice age 18-22, cheerful";

    public ObservableCollection<StoryboardClip> Clips { get; } = new();
    public FacebookPost Facebook { get; } = new();

    public int ClipCount => Clips.Count;
}
