using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  Seedance 2.0 prompt wizard.
//
//  This is a *prompt authoring* tool, not a generation route — there is no
//  Seedance API wired into the studio. Every control on the wizard feeds the
//  single assembled prompt string the user sees on the right (and can Copy or
//  Send to the Composer). Nothing here fakes a counter or a provider, per the
//  studio's "no UI lies" rule.
//
//  The model knowledge encoded here (the @reference system, the golden
//  formula, the lip-sync constraints) comes from digen.ai's Seedance 2.0
//  launch guide + seedance2.ai/guide + the community audio/lip-sync guides.
// ─────────────────────────────────────────────────────────────────────────────

public enum SeedanceMode { Storyboard, LipSync }

public enum MaterialKind { Image, Video, Audio }

/// <summary>One uploaded reference material. Seedance applies a material only
/// when it is named with an @reference AND given a clear purpose ("role"), so
/// both the auto <see cref="RefName"/> and the user-typed <see cref="Role"/>
/// feed the prompt.</summary>
public sealed class SeedMaterial : ObservableObject
{
    public MaterialKind Kind { get; init; }

    private int _index = 1;
    public int Index
    {
        get => _index;
        set { if (SetProperty(ref _index, value)) OnPropertyChanged(nameof(RefName)); }
    }

    /// <summary>e.g. <c>@Image1</c>, <c>@Video2</c>, <c>@Audio1</c>.</summary>
    public string RefName => $"@{Kind}{Index}";

    public string KindName => Kind.ToString();

    private string _label = "";
    /// <summary>Filename (when attached) or a free label. Display only.</summary>
    public string Label { get => _label; set => SetProperty(ref _label, value); }

    /// <summary>Full path of the attached file, if one was picked — what the
    /// Composer needs to upload it as the reference image.</summary>
    public string? FilePath { get; set; }

    private string _role = "";
    /// <summary>What this material is FOR — "first frame", "main character",
    /// "camera movement", "voice tone"… This is the half Seedance users most
    /// often forget, so the wizard surfaces it as its own field.</summary>
    public string Role { get => _role; set => SetProperty(ref _role, value); }

    public string Glyph => Kind switch
    {
        MaterialKind.Image => "IcoImage",
        MaterialKind.Video => "IcoFilm",
        _ => "IcoAudioWave",
    };

    public string RoleHint => Kind switch
    {
        MaterialKind.Image => "first frame · main character · scene · logo · last frame",
        MaterialKind.Video => "camera movement · action · effects · voice tone · extend",
        _ => "rhythm / beat · voice tone · ambience",
    };
}

/// <summary>A "0–3s: …" timeline beat. Seedance grasps pacing best when the
/// shot is broken into second-by-second segments.</summary>
public sealed class SeedSegment : ObservableObject
{
    private double _start;
    public double Start { get => _start; set { if (SetProperty(ref _start, value)) OnPropertyChanged(nameof(Range)); } }

    private double _end = 3;
    public double End { get => _end; set { if (SetProperty(ref _end, value)) OnPropertyChanged(nameof(Range)); } }

    private string _text = "";
    public string Text { get => _text; set => SetProperty(ref _text, value); }

    public string Range => $"{Start:0}-{End:0}s";
}

/// <summary>One spoken line for the lip-sync / dialogue mode.</summary>
public sealed class SeedLine : ObservableObject
{
    private string _speaker = "Character A";
    public string Speaker { get => _speaker; set => SetProperty(ref _speaker, value); }

    private string _language = "English";
    public string Language { get => _language; set => SetProperty(ref _language, value); }

    private string _text = "";
    public string Text { get => _text; set { if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(WordCount)); } }

    private string _emotion = "";
    /// <summary>Optional emotion / delivery cue — "calm, warm" / "excited,
    /// breathy". Sets expected mouth openness &amp; breathing.</summary>
    public string Emotion { get => _emotion; set => SetProperty(ref _emotion, value); }

    public int WordCount => string.IsNullOrWhiteSpace(Text)
        ? 0
        : Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>Generic single-select chip (shot type, camera move, aspect,
/// resolution, use-case template). Data-driven so the XAML stays an
/// ItemsControl instead of dozens of hand-written RadioButtons.</summary>
public sealed class PickOption : ObservableObject
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

/// <summary>A Seedance use-case. Selecting one tailors the hint, may switch
/// mode (lip-sync), and seeds a recommended camera move — a real action, not
/// decoration.</summary>
public sealed class SeedTemplate : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Blurb { get; init; } = "";
    /// <summary>Lead-in clause prepended to the assembled prompt's purpose.</summary>
    public string Purpose { get; init; } = "";
    public bool SwitchesToLipSync { get; init; }
    public string? RecommendedMove { get; init; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

public sealed class SeedanceWizardViewModel : ObservableObject
{
    public const int StepCount = 5;

    public SeedanceWizardViewModel()
    {
        Templates = new ObservableCollection<SeedTemplate>(BuildTemplates());
        ShotTypes = new ObservableCollection<PickOption>(BuildShots());
        CameraMoves = new ObservableCollection<PickOption>(BuildMoves());
        Aspects = new ObservableCollection<PickOption>(BuildAspects());
        Resolutions = new ObservableCollection<PickOption>(BuildResolutions());

        Materials.CollectionChanged += OnWatchedCollectionChanged;
        Segments.CollectionChanged += OnWatchedCollectionChanged;
        Lines.CollectionChanged += OnWatchedCollectionChanged;

        // Commands
        SetModeCommand = new RelayCommand<string>(SetMode);
        SelectTemplateCommand = new RelayCommand<string>(SelectTemplate);
        // Each pick rebuilds the prompt: the chip used to light up while the
        // copied prompt still carried the previous lens, move, frame or size.
        SelectShotCommand = new RelayCommand<string>(id => { SelectFrom(ShotTypes, id); Recompute(); });
        SelectMoveCommand = new RelayCommand<string>(id => { SelectFrom(CameraMoves, id); Recompute(); });
        SelectAspectCommand = new RelayCommand<string>(id => { SelectFrom(Aspects, id); Recompute(); });
        SelectResolutionCommand = new RelayCommand<string>(id => { SelectFrom(Resolutions, id); Recompute(); });

        AddMaterialCommand = new RelayCommand<string>(AddMaterial);
        RemoveMaterialCommand = new RelayCommand<SeedMaterial>(RemoveMaterial);
        AddSegmentCommand = new RelayCommand(AddSegment);
        RemoveSegmentCommand = new RelayCommand<SeedSegment>(s => { if (s is not null) Segments.Remove(s); });
        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand<SeedLine>(l => { if (l is not null) Lines.Remove(l); });

        NextStepCommand = new RelayCommand(() => CurrentStep = Math.Min(StepCount, CurrentStep + 1), () => CurrentStep < StepCount);
        PrevStepCommand = new RelayCommand(() => CurrentStep = Math.Max(1, CurrentStep - 1), () => CurrentStep > 1);
        GoToStepCommand = new RelayCommand<string>(s => { if (int.TryParse(s, out var n)) CurrentStep = Math.Clamp(n, 1, StepCount); });
        CopyPromptCommand = new RelayCommand(CopyPrompt);
        ResetCommand = new RelayCommand(SeedDefaults);

        SeedDefaults();
    }

    // ── Step machine ─────────────────────────────────────────────────────────
    private int _currentStep = 1;
    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            if (!SetProperty(ref _currentStep, value)) return;
            OnPropertyChanged(nameof(IsStep1));
            OnPropertyChanged(nameof(IsStep2));
            OnPropertyChanged(nameof(IsStep3));
            OnPropertyChanged(nameof(IsStep4));
            OnPropertyChanged(nameof(IsStep5));
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepCaption));
            NextStepCommand.NotifyCanExecuteChanged();
            PrevStepCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;

    public string StepTitle => CurrentStep switch
    {
        1 => "Mode & use-case",
        2 => "Materials  ·  @references",
        3 => IsLipSync ? "Dialogue & camera" : "Scene & camera",
        4 => IsLipSync ? "Voice & sound" : "Timeline & sound",
        _ => "Output & review",
    };

    public string StepCaption => CurrentStep switch
    {
        1 => "Pick how you'll direct this shot.",
        2 => "Seedance only applies a material that is @referenced AND given a role.",
        3 => IsLipSync
            ? "Quote each line, keep it 5–10 words, lock the camera."
            : "Say who does what, then choose the lens & move.",
        4 => IsLipSync
            ? "Reference a voice, add SFX / ambience / music."
            : "Break the shot into beats, then layer sound.",
        _ => "Set length & frame, then copy the prompt.",
    };

    // ── Mode ─────────────────────────────────────────────────────────────────
    private SeedanceMode _mode = SeedanceMode.Storyboard;
    public SeedanceMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsStoryboard));
            OnPropertyChanged(nameof(IsLipSync));
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepCaption));
            Recompute();
        }
    }
    public bool IsStoryboard => _mode == SeedanceMode.Storyboard;
    public bool IsLipSync => _mode == SeedanceMode.LipSync;

    private void SetMode(string? id)
    {
        if (string.Equals(id, "LipSync", StringComparison.OrdinalIgnoreCase)) Mode = SeedanceMode.LipSync;
        else Mode = SeedanceMode.Storyboard;
    }

    // ── Use-case templates ───────────────────────────────────────────────────
    public ObservableCollection<SeedTemplate> Templates { get; }

    private string _selectedTemplateId = "free";
    public string SelectedTemplateId { get => _selectedTemplateId; private set => SetProperty(ref _selectedTemplateId, value); }

    public SeedTemplate? ActiveTemplate => Templates.FirstOrDefault(t => t.Id == SelectedTemplateId);

    private void SelectTemplate(string? id)
    {
        if (id is null) return;
        SelectedTemplateId = id;
        foreach (var t in Templates) t.IsActive = t.Id == id;
        var tpl = ActiveTemplate;
        if (tpl is not null)
        {
            if (tpl.SwitchesToLipSync) Mode = SeedanceMode.LipSync;
            else if (id is "free" or "storyboard" or "first-last" or "consistency" or "scene-ref" or "single-take")
                Mode = SeedanceMode.Storyboard;
            if (tpl.RecommendedMove is not null) SelectFrom(CameraMoves, tpl.RecommendedMove);
        }
        OnPropertyChanged(nameof(ActiveTemplate));
        Recompute();
    }

    // ── Description (storyboard) ──────────────────────────────────────────────
    private string _subject = "";
    public string Subject { get => _subject; set { if (SetProperty(ref _subject, value)) Recompute(); } }

    private string _action = "";
    public string Action { get => _action; set { if (SetProperty(ref _action, value)) Recompute(); } }

    private string _scene = "";
    /// <summary>Environment, lighting & visual style — the optional half of the
    /// formula.</summary>
    public string Scene { get => _scene; set { if (SetProperty(ref _scene, value)) Recompute(); } }

    private string _textOverlay = "";
    /// <summary>On-screen text / slogan / subtitle (optional).</summary>
    public string TextOverlay { get => _textOverlay; set { if (SetProperty(ref _textOverlay, value)) Recompute(); } }

    // ── Camera ────────────────────────────────────────────────────────────────
    public ObservableCollection<PickOption> ShotTypes { get; }
    public ObservableCollection<PickOption> CameraMoves { get; }

    // ── Lip-sync ──────────────────────────────────────────────────────────────
    public ObservableCollection<SeedLine> Lines { get; } = new();

    private string _speakingStyle = "";
    /// <summary>Global delivery note — "deliberate enunciation", "fast-paced".</summary>
    public string SpeakingStyle { get => _speakingStyle; set { if (SetProperty(ref _speakingStyle, value)) Recompute(); } }

    private bool _lockCamera = true;
    /// <summary>Lip-sync works best with a locked medium close-up. On by
    /// default; turning it off raises a soft warning.</summary>
    public bool LockCamera { get => _lockCamera; set { if (SetProperty(ref _lockCamera, value)) Recompute(); } }

    // ── Sound ─────────────────────────────────────────────────────────────────
    private string _sfx = "";
    public string Sfx { get => _sfx; set { if (SetProperty(ref _sfx, value)) Recompute(); } }

    private string _ambience = "";
    public string Ambience { get => _ambience; set { if (SetProperty(ref _ambience, value)) Recompute(); } }

    private string _voiceover = "";
    public string Voiceover { get => _voiceover; set { if (SetProperty(ref _voiceover, value)) Recompute(); } }

    private string _voiceToneRef = "";
    /// <summary>Which material the voice tone should copy, e.g. "@Video1".</summary>
    public string VoiceToneRef { get => _voiceToneRef; set { if (SetProperty(ref _voiceToneRef, value)) Recompute(); } }

    private bool _backgroundMusic;
    public bool BackgroundMusic { get => _backgroundMusic; set { if (SetProperty(ref _backgroundMusic, value)) Recompute(); } }

    // ── Timeline ──────────────────────────────────────────────────────────────
    public ObservableCollection<SeedSegment> Segments { get; } = new();

    private bool _useTimeline;
    public bool UseTimeline { get => _useTimeline; set { if (SetProperty(ref _useTimeline, value)) Recompute(); } }

    // ── Output ────────────────────────────────────────────────────────────────
    public ObservableCollection<PickOption> Aspects { get; }
    public ObservableCollection<PickOption> Resolutions { get; }

    private double _durationSec = 6;
    public double DurationSec { get => _durationSec; set { if (SetProperty(ref _durationSec, Math.Round(value))) { OnPropertyChanged(nameof(DurationLabel)); Recompute(); } } }
    public string DurationLabel => $"{DurationSec:0}s";

    // ── Assembled output + validation ────────────────────────────────────────
    private string _assembledPrompt = "";
    public string AssembledPrompt { get => _assembledPrompt; private set { if (SetProperty(ref _assembledPrompt, value)) OnPropertyChanged(nameof(CharCount)); } }
    public int CharCount => _assembledPrompt.Length;

    public ObservableCollection<string> Warnings { get; } = new();
    public bool HasWarnings => Warnings.Count > 0;

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    // ── Commands ──────────────────────────────────────────────────────────────
    public IRelayCommand<string> SetModeCommand { get; }
    public IRelayCommand<string> SelectTemplateCommand { get; }
    public IRelayCommand<string> SelectShotCommand { get; }
    public IRelayCommand<string> SelectMoveCommand { get; }
    public IRelayCommand<string> SelectAspectCommand { get; }
    public IRelayCommand<string> SelectResolutionCommand { get; }
    public IRelayCommand<string> AddMaterialCommand { get; }
    public IRelayCommand<SeedMaterial> RemoveMaterialCommand { get; }
    public IRelayCommand AddSegmentCommand { get; }
    public IRelayCommand<SeedSegment> RemoveSegmentCommand { get; }
    public IRelayCommand AddLineCommand { get; }
    public IRelayCommand<SeedLine> RemoveLineCommand { get; }
    public IRelayCommand NextStepCommand { get; }
    public IRelayCommand PrevStepCommand { get; }
    public IRelayCommand<string> GoToStepCommand { get; }
    public IRelayCommand CopyPromptCommand { get; }
    public IRelayCommand ResetCommand { get; }

    // ── Materials ─────────────────────────────────────────────────────────────
    public ObservableCollection<SeedMaterial> Materials { get; } = new();
    public bool HasNoMaterials => Materials.Count == 0;

    private static readonly (MaterialKind kind, int max)[] Caps =
    {
        (MaterialKind.Image, 9), (MaterialKind.Video, 3), (MaterialKind.Audio, 3),
    };

    /// <summary>Add an empty reference row of the given kind. The user can
    /// attach a file and type a role afterwards. Public so the view's
    /// file-browse handler can pass a label/path.</summary>
    public SeedMaterial AddMaterial(string? kindName, string? label = null)
    {
        var kind = kindName switch
        {
            "Video" => MaterialKind.Video,
            "Audio" => MaterialKind.Audio,
            _ => MaterialKind.Image,
        };
        var mat = new SeedMaterial { Kind = kind, Label = label ?? "" };
        Materials.Add(mat);
        Reindex();
        return mat;
    }

    private void AddMaterial(string? kindName) => AddMaterial(kindName, null);

    private void RemoveMaterial(SeedMaterial? mat)
    {
        if (mat is null) return;
        Materials.Remove(mat);
        Reindex();
    }

    /// <summary>Renumber each kind 1..N in collection order so the @refs stay
    /// gap-free after an insert or delete.</summary>
    private void Reindex()
    {
        foreach (var (kind, _) in Caps)
        {
            var i = 1;
            foreach (var m in Materials.Where(m => m.Kind == kind))
                m.Index = i++;
        }
        Recompute();
    }

    private void AddSegment()
    {
        var last = Segments.LastOrDefault();
        var start = last is null ? 0 : last.End;
        Segments.Add(new SeedSegment { Start = start, End = start + 3, Text = "" });
    }

    private void AddLine() => Lines.Add(new SeedLine { Speaker = $"Character {(char)('A' + Lines.Count % 26)}" });

    private static void SelectFrom(ObservableCollection<PickOption> set, string? id)
    {
        foreach (var o in set) o.IsActive = o.Id == id;
    }

    private void CopyPrompt()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.IsNullOrWhiteSpace(AssembledPrompt) ? " " : AssembledPrompt);
            StatusMessage = "Prompt copied to clipboard ✓";
        }
        catch
        {
            StatusMessage = "Couldn't reach the clipboard — try again.";
        }
    }

    // ── Recompute (the heart) ────────────────────────────────────────────────
    private void OnWatchedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var o in e.OldItems.OfType<INotifyPropertyChanged>())
                o.PropertyChanged -= OnWatchedItemChanged;
        if (e.NewItems is not null)
            foreach (var o in e.NewItems.OfType<INotifyPropertyChanged>())
                o.PropertyChanged += OnWatchedItemChanged;
        OnPropertyChanged(nameof(HasNoMaterials));
        Recompute();
    }

    private void OnWatchedItemChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    private void Recompute()
    {
        AssembledPrompt = BuildPrompt();
        RebuildWarnings();
    }

    private string ActiveLabel(ObservableCollection<PickOption> set) => set.FirstOrDefault(o => o.IsActive)?.Label ?? "";

    /// <summary>The picked frame, for the Composer hand-off.</summary>
    public string ActiveAspectId => Aspects.FirstOrDefault(o => o.IsActive)?.Id ?? "16:9";

    /// <summary>The picked camera move and output size, for the Composer hand-off.</summary>
    public string ActiveMoveId => CameraMoves.FirstOrDefault(o => o.IsActive)?.Id ?? "locked";
    public string ActiveResolutionId => Resolutions.FirstOrDefault(o => o.IsActive)?.Id ?? "1080p";

    /// <summary>
    /// The attached image the Composer should upload as its reference: the one
    /// whose role names the first frame or the character, else the first image.
    /// </summary>
    public string? PrimaryImagePath
    {
        get
        {
            var images = Materials.Where(m => m.Kind == MaterialKind.Image
                                              && !string.IsNullOrEmpty(m.FilePath) && System.IO.File.Exists(m.FilePath)).ToList();
            return (images.FirstOrDefault(m => m.Role.Contains("first frame", StringComparison.OrdinalIgnoreCase))
                    ?? images.FirstOrDefault(m => m.Role.Contains("character", StringComparison.OrdinalIgnoreCase))
                    ?? images.FirstOrDefault())?.FilePath;
        }
    }

    private string BuildPrompt()
    {
        var sb = new StringBuilder();

        // 1) @reference clause — every material that has a role.
        var refs = Materials
            .Where(m => !string.IsNullOrWhiteSpace(m.Role))
            .Select(m => $"{m.RefName} as {m.Role.Trim()}")
            .ToList();
        if (refs.Count > 0)
            sb.Append(string.Join("; ", refs)).Append(". ");

        // 2) Purpose lead-in from the chosen use-case.
        var tpl = ActiveTemplate;
        if (tpl is not null && !string.IsNullOrWhiteSpace(tpl.Purpose))
            sb.Append(tpl.Purpose.Trim()).Append(". ");

        if (IsLipSync)
        {
            // Subject (the speaker portrait) is optional context here.
            if (!string.IsNullOrWhiteSpace(Subject))
                sb.Append(Subject.Trim()).Append(". ");

            // Camera — lip-sync wants a locked medium close-up.
            if (LockCamera)
                sb.Append("Locked medium close-up, front-facing, no head movement. ");
            else
            {
                var shot = ActiveLabel(ShotTypes);
                if (!string.IsNullOrWhiteSpace(shot)) sb.Append(shot).Append(". ");
            }

            // Lines.
            foreach (var l in Lines)
            {
                if (string.IsNullOrWhiteSpace(l.Text)) continue;
                sb.Append(l.Speaker.Trim());
                sb.Append(" says");
                if (!string.IsNullOrWhiteSpace(l.Language) &&
                    !string.Equals(l.Language, "English", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" in ").Append(l.Language.Trim());
                sb.Append(": \"").Append(l.Text.Trim().Trim('"')).Append('"');
                if (!string.IsNullOrWhiteSpace(l.Emotion)) sb.Append(" (").Append(l.Emotion.Trim()).Append(')');
                sb.Append(". ");
            }

            if (!string.IsNullOrWhiteSpace(SpeakingStyle))
                sb.Append(SpeakingStyle.Trim()).Append(". ");
            if (!string.IsNullOrWhiteSpace(VoiceToneRef))
                sb.Append("Reference voice tone from ").Append(VoiceToneRef.Trim()).Append(". ");
        }
        else
        {
            // Subject + motion (the required half).
            var core = string.Join(" ", new[] { Subject, Action }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            if (!string.IsNullOrWhiteSpace(core)) sb.Append(core).Append(". ");

            if (!string.IsNullOrWhiteSpace(Scene)) sb.Append(Scene.Trim()).Append(". ");

            var shot = ActiveLabel(ShotTypes);
            var move = ActiveLabel(CameraMoves);
            var cam = string.Join(", ", new[] { shot, move }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrWhiteSpace(cam)) sb.Append(cam).Append(". ");

            if (UseTimeline)
            {
                foreach (var seg in Segments)
                {
                    if (string.IsNullOrWhiteSpace(seg.Text)) continue;
                    sb.Append('\n').Append(seg.Range).Append(": ").Append(seg.Text.Trim()).Append('.');
                }
                if (Segments.Any(s => !string.IsNullOrWhiteSpace(s.Text))) sb.Append('\n');
            }
        }

        // Shared: on-screen text + sound bed.
        if (!string.IsNullOrWhiteSpace(TextOverlay))
            sb.Append("On-screen text: \"").Append(TextOverlay.Trim().Trim('"')).Append("\". ");

        var sound = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(Voiceover)) sound.Add($"voiceover: {Voiceover.Trim()}");
        if (!string.IsNullOrWhiteSpace(Sfx)) sound.Add($"SFX: {Sfx.Trim()}");
        if (!string.IsNullOrWhiteSpace(Ambience)) sound.Add($"ambience: {Ambience.Trim()}");
        if (BackgroundMusic) sound.Add("background music");
        if (sound.Count > 0)
            sb.Append(char.ToUpper(sound[0][0])).Append(sound[0].AsSpan(1)).Append(sound.Count > 1 ? "; " + string.Join("; ", sound.Skip(1)) : "").Append(". ");

        // Technical tail.
        var aspect = ActiveLabel(Aspects);
        var res = ActiveLabel(Resolutions);
        var tail = string.Join(" · ", new[] { aspect, res, $"{DurationSec:0}s" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(tail)) sb.Append(tail).Append('.');

        return sb.ToString().Trim();
    }

    private void RebuildWarnings()
    {
        Warnings.Clear();

        foreach (var (kind, max) in Caps)
        {
            var n = Materials.Count(m => m.Kind == kind);
            if (n > max) Warnings.Add($"Seedance allows ≤ {max} {kind.ToString().ToLower()} files — you have {n}.");
        }
        if (Materials.Count > 12)
            Warnings.Add($"≤ 12 materials total across all types — you have {Materials.Count}.");

        foreach (var m in Materials.Where(m => string.IsNullOrWhiteSpace(m.Role)))
            Warnings.Add($"{m.RefName} has no role — Seedance won't apply a material that isn't given a purpose.");

        if (DurationSec is < 4 or > 15)
            Warnings.Add("Generation length is usually 4–15s.");

        if (IsLipSync)
        {
            if (Lines.All(l => string.IsNullOrWhiteSpace(l.Text)))
                Warnings.Add("Add at least one quoted line for the character to speak.");
            foreach (var l in Lines.Where(l => l.WordCount > 10))
                Warnings.Add($"\"{Trim(l.Text)}\" is {l.WordCount} words — keep lines to 5–10 for clean lip-sync.");
            if (!LockCamera)
                Warnings.Add("Lip-sync drifts without a locked camera — head/camera motion fights the mouth engine.");
            if (ContainsHeadMotion(Subject) || Lines.Any(l => ContainsHeadMotion(l.Text)))
                Warnings.Add("Avoid 'nod' / 'turn head' in dialogue shots — it competes with lip-sync.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(Action))
                Warnings.Add("Name the subject and its motion — that's the one required half of the formula.");
        }

        OnPropertyChanged(nameof(HasWarnings));
    }

    private static bool ContainsHeadMotion(string? s) =>
        !string.IsNullOrWhiteSpace(s) &&
        (s.Contains("nod", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("turn head", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("shake head", StringComparison.OrdinalIgnoreCase));

    private static string Trim(string s) => s.Length <= 28 ? s : s[..28] + "…";

    // ── Seed data ─────────────────────────────────────────────────────────────
    private void SeedDefaults()
    {
        Mode = SeedanceMode.Storyboard;
        CurrentStep = 1;
        SelectTemplate("free");
        SelectFrom(ShotTypes, "medium");
        SelectFrom(CameraMoves, "push");
        SelectFrom(Aspects, "16:9");
        SelectFrom(Resolutions, "1080p");
        DurationSec = 6;

        Materials.Clear();
        Segments.Clear();
        Lines.Clear();

        var hero = AddMaterial("Image", "empress-portrait.png");
        hero.Role = "first frame";

        Subject = "ราชินีจันทรา, a moonlit queen in crimson silk";
        Action = "slowly rises from a throne of black lotuses, a gold halo splitting into eight beams";
        Scene = "volumetric moonlight, drifting incense smoke, cinematic and dreamy";
        TextOverlay = "";
        Sfx = "low temple bell, distant chimes";
        Ambience = "";
        Voiceover = "";
        VoiceToneRef = "";
        SpeakingStyle = "";
        BackgroundMusic = true;
        UseTimeline = false;
        Lines.Add(new SeedLine { Speaker = "Anya", Language = "English", Text = "The night is ours.", Emotion = "calm, warm" });
        StatusMessage = "";
        Recompute();
    }

    private static SeedTemplate[] BuildTemplates() => new[]
    {
        new SeedTemplate { Id = "free", Name = "Freeform", Blurb = "Just describe the shot.", Purpose = "" },
        new SeedTemplate { Id = "first-last", Name = "First / last frame", Blurb = "Pin the opening (and closing) frame to an image.", Purpose = "Open on the first-frame image and animate forward", RecommendedMove = "push" },
        new SeedTemplate { Id = "consistency", Name = "Character consistency", Blurb = "Keep one character across shots from reference images.", Purpose = "Keep the referenced character's face, hair and wardrobe consistent" },
        new SeedTemplate { Id = "action-ref", Name = "Action reference", Blurb = "Copy a motion from a reference video.", Purpose = "Make the subject mirror the referenced action" },
        new SeedTemplate { Id = "camera-ref", Name = "Camera language", Blurb = "Transfer camera moves from a video.", Purpose = "Reproduce the referenced camera movement and transitions", RecommendedMove = "follow" },
        new SeedTemplate { Id = "scene-ref", Name = "Scene reference", Blurb = "Compose a scene from reference frames.", Purpose = "Build the scene from the referenced frames" },
        new SeedTemplate { Id = "single-take", Name = "Single-take multi-scene", Blurb = "One continuous tracking shot through several scenes.", Purpose = "One continuous single-take tracking shot through the scenes in order", RecommendedMove = "onetake" },
        new SeedTemplate { Id = "extend", Name = "Video extension", Blurb = "Add seconds before / after an existing clip.", Purpose = "Extend the referenced clip — set the length to the ADDED segment only" },
        new SeedTemplate { Id = "replace", Name = "Element / character swap", Blurb = "Swap a subject while keeping motion & camera.", Purpose = "Replace the referenced element while keeping the original motion and camera" },
        new SeedTemplate { Id = "merge", Name = "Multi-modal merge", Blurb = "Bridge two clips with a new middle scene.", Purpose = "Add a connecting scene between the referenced clips" },
        new SeedTemplate { Id = "beat", Name = "Beat sync", Blurb = "Cut images to an audio beat.", Purpose = "Sync the cuts and motion to the beat of the referenced audio" },
        new SeedTemplate { Id = "lipsync", Name = "Talking head (lip-sync)", Blurb = "A character speaks your lines in sync.", Purpose = "A talking-head shot with accurate lip-sync", SwitchesToLipSync = true, RecommendedMove = "locked" },
    };

    private static PickOption[] BuildShots() => new[]
    {
        new PickOption { Id = "closeup", Label = "close-up", Hint = "tight on the face" },
        new PickOption { Id = "mcu", Label = "medium close-up", Hint = "head & shoulders" },
        new PickOption { Id = "medium", Label = "medium shot", Hint = "waist up" },
        new PickOption { Id = "wide", Label = "wide shot", Hint = "full scene" },
        new PickOption { Id = "ots", Label = "over-the-shoulder", Hint = "behind a subject" },
        new PickOption { Id = "fpv", Label = "first-person", Hint = "POV" },
        new PickOption { Id = "low", Label = "low angle", Hint = "looking up" },
        new PickOption { Id = "bird", Label = "bird's-eye", Hint = "top-down" },
    };

    private static PickOption[] BuildMoves() => new[]
    {
        new PickOption { Id = "locked", Label = "locked", Hint = "no movement" },
        new PickOption { Id = "push", Label = "push in", Hint = "dolly toward" },
        new PickOption { Id = "pull", Label = "pull back", Hint = "dolly away" },
        new PickOption { Id = "panL", Label = "pan left", Hint = "" },
        new PickOption { Id = "panR", Label = "pan right", Hint = "" },
        new PickOption { Id = "tilt", Label = "tilt", Hint = "up / down" },
        new PickOption { Id = "orbit", Label = "orbit", Hint = "rotate around" },
        new PickOption { Id = "follow", Label = "follow", Hint = "track the subject" },
        new PickOption { Id = "onetake", Label = "one-take", Hint = "continuous" },
        new PickOption { Id = "dolly", Label = "dolly zoom", Hint = "Hitchcock" },
    };

    private static PickOption[] BuildAspects() => new[]
    {
        new PickOption { Id = "9:16", Label = "9:16", Hint = "vertical" },
        new PickOption { Id = "1:1", Label = "1:1", Hint = "square" },
        new PickOption { Id = "16:9", Label = "16:9", Hint = "wide" },
        new PickOption { Id = "21:9", Label = "21:9", Hint = "cinema" },
    };

    private static PickOption[] BuildResolutions() => new[]
    {
        new PickOption { Id = "480p", Label = "480p", Hint = "draft" },
        new PickOption { Id = "720p", Label = "720p", Hint = "" },
        new PickOption { Id = "1080p", Label = "1080p", Hint = "HD" },
    };
}
