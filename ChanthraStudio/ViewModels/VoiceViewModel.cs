using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class VoiceProviderOption
{
    public IVoiceProvider Provider { get; init; } = null!;
    public string Id => Provider.Id;
    public string DisplayName => Provider.DisplayName;

    /// <summary>
    /// The app-wide ComboBox template drives its closed state from
    /// SelectionBoxItemTemplate, which mirrors ItemTemplate only — so a picker
    /// configured with DisplayMemberPath falls back to ToString() and shows the
    /// type name. Same convention as <see cref="Models.WebTool"/> and
    /// <see cref="VideoRouteOption"/>.
    /// </summary>
    public override string ToString() => DisplayName;
}

public sealed class MusicProviderOption
{
    public IMusicProvider Provider { get; init; } = null!;
    public string Id => Provider.Id;
    public string DisplayName => Provider.DisplayName;

    /// <inheritdoc cref="VoiceProviderOption.ToString"/>
    public override string ToString() => DisplayName;
}

/// <summary>
/// "TTS" = synthesise speech via OpenAI/ElevenLabs.
/// "Music" = generate music via Replicate (musicgen / ace-step / riffusion).
/// </summary>
public enum VoiceMode { Tts, Music }

public sealed class VoiceViewModel : ObservableObject
{
    private readonly StudioContext _ctx;

    public ObservableCollection<VoiceProviderOption> Providers { get; } = new();
    public ObservableCollection<VoicePreset> Voices { get; } = new();
    public ObservableCollection<VoiceTake> Takes { get; } = new();

    public ObservableCollection<MusicProviderOption> MusicProviders { get; } = new();
    public ObservableCollection<VoiceTake> MusicTakes { get; } = new();

    private VoiceMode _mode = VoiceMode.Tts;
    public VoiceMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsTtsMode));
                OnPropertyChanged(nameof(IsMusicMode));
            }
        }
    }

    public bool IsTtsMode => _mode == VoiceMode.Tts;
    public bool IsMusicMode => _mode == VoiceMode.Music;

    private VoiceProviderOption? _selectedProvider;
    public VoiceProviderOption? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!SetProperty(ref _selectedProvider, value)) return;
            ReloadVoices();
            OnPropertyChanged(nameof(ShowCustomVoiceField));
            OnPropertyChanged(nameof(SupportsSpeed));
            OnPropertyChanged(nameof(SupportsStability));
            SyncStabilityStep();
        }
    }

    private VoicePreset? _selectedVoice;
    public VoicePreset? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (SetProperty(ref _selectedVoice, value))
                OnPropertyChanged(nameof(EffectiveVoiceLabel));
        }
    }

    private string _voicesNote = "";
    /// <summary>
    /// Where the voice list came from when it is not the vendor's live list
    /// — so a built-in fallback is never presented as the account's voices.
    /// </summary>
    public string VoicesNote
    {
        get => _voicesNote;
        private set
        {
            if (SetProperty(ref _voicesNote, value))
                OnPropertyChanged(nameof(HasVoicesNote));
        }
    }
    public bool HasVoicesNote => !string.IsNullOrEmpty(_voicesNote);

    /// <summary>
    /// User-pasted cloned voice id (ElevenLabs only). When non-empty, the
    /// generate command uses it instead of <see cref="SelectedVoice"/>, so
    /// owners of cloned/professional voices can hit them without the
    /// default library. Persists via Settings as "elevenlabs_custom_voice"
    /// so it survives relaunch.
    /// </summary>
    private string _customVoiceId = "";
    public string CustomVoiceId
    {
        get => _customVoiceId;
        set
        {
            if (!SetProperty(ref _customVoiceId, value ?? "")) return;
            _ctx.Settings.SetSetting("elevenlabs_custom_voice", _customVoiceId);
            try { _ctx.Settings.Save(); } catch { /* best-effort */ }
            OnPropertyChanged(nameof(HasCustomVoice));
            OnPropertyChanged(nameof(EffectiveVoiceLabel));
        }
    }

    public bool HasCustomVoice => !string.IsNullOrWhiteSpace(_customVoiceId);

    /// <summary>"Sarah" or "custom · 21m00…" — what the user sees as the
    /// effective voice line in the synth meta.</summary>
    public string EffectiveVoiceLabel
    {
        get
        {
            if (HasCustomVoice)
            {
                var trimmed = _customVoiceId.Length > 8 ? _customVoiceId[..8] + "…" : _customVoiceId;
                return $"custom · {trimmed}";
            }
            return _selectedVoice?.DisplayName ?? "—";
        }
    }

    public bool ShowCustomVoiceField => _selectedProvider?.Id == "elevenlabs";

    // Each slider shows only where the provider reads it: OpenAI takes a
    // speed and no stability, the ElevenLabs call sends stability only.
    public bool SupportsSpeed => _selectedProvider?.Id != "elevenlabs";
    public bool SupportsStability => _selectedProvider?.Id == "elevenlabs";

    /// <summary>
    /// Eleven v3 takes only 0 / 0.5 / 1 (Creative, Natural, Robust); the
    /// provider rounds to those, so the slider snaps to them too — it used to
    /// read 0.30 while 0.5 was sent.
    /// </summary>
    public double StabilityStep
    {
        get
        {
            if (_selectedProvider?.Id != "elevenlabs") return 0.01;
            var model = _ctx.Settings.GetSetting("activeModel:elevenlabs");
            if (string.IsNullOrWhiteSpace(model)) model = _selectedProvider.Provider.DefaultModelId;
            return model == "eleven_v3" ? 0.5 : 0.01;
        }
    }

    private void SyncStabilityStep()
    {
        OnPropertyChanged(nameof(StabilityStep));
        if (StabilityStep >= 0.5)
            Stability = Math.Round(Stability * 2, MidpointRounding.AwayFromZero) / 2;
    }

    private MusicProviderOption? _selectedMusicProvider;
    public MusicProviderOption? SelectedMusicProvider
    {
        get => _selectedMusicProvider;
        set
        {
            if (!SetProperty(ref _selectedMusicProvider, value)) return;
            OnPropertyChanged(nameof(IsAceStepRoute));
            OnPropertyChanged(nameof(MusicRouteHint));
        }
    }

    /// <summary>
    /// True for the two ComfyUI routes, which take a tag list plus optional
    /// sung lyrics rather than a model slug. Drives the lyrics editor's
    /// visibility — showing a lyrics box for MusicGen, which cannot sing,
    /// would be an offer the route can't honour.
    /// </summary>
    public bool IsAceStepRoute
        => _selectedMusicProvider?.Id is "comfyui-music" or "rentgpu-music";

    /// <summary>One line telling the user what this route will cost them.</summary>
    public string MusicRouteHint => _selectedMusicProvider?.Id switch
    {
        "comfyui-music" => "ฟรี — รันบนการ์ดตัวเอง ต้องมี ace_step_v1_3.5b.safetensors ใน models/checkpoints",
        "rentgpu-music" => "เช่าการ์ด 8GB — สตูดิโอโหลด ACE-Step (7.17 GB) ให้เอง คิดเงินตามเวลาที่เครื่องเปิด",
        _ => "คิดเงินต่อวินาทีของเพลงที่ได้ ผ่านบัญชี Replicate",
    };

    private string _musicModel = "meta/musicgen";
    /// <summary>Replicate model slug for music (owner/name).</summary>
    public string MusicModel
    {
        get => _musicModel;
        set => SetProperty(ref _musicModel, value);
    }

    private string _musicPrompt = "epic cinematic orchestra · slow build · 90 BPM · gold strings · empress theme";
    public string MusicPrompt { get => _musicPrompt; set => SetProperty(ref _musicPrompt, value); }

    private double _musicDuration = 30.0;
    public double MusicDuration
    {
        get => _musicDuration;
        set => SetProperty(ref _musicDuration, Math.Clamp(value, 4, 240));
    }

    private string _musicLyrics = "";
    /// <summary>
    /// Sung lyrics for the ACE-Step routes. Empty = instrumental, which is a
    /// real choice rather than a missing value, so it is passed through as an
    /// empty string rather than skipped.
    /// </summary>
    public string MusicLyrics { get => _musicLyrics; set => SetProperty(ref _musicLyrics, value); }

    private string _musicStage = "";
    /// <summary>Warm-up stage text while a card is being rented for music.
    /// Empty when nothing is warming up.</summary>
    public string MusicStage { get => _musicStage; set => SetProperty(ref _musicStage, value); }

    private string _scriptText = "ราชินีจันทรา ดวงประจำวันที่ปลายเดือน — เปิดประตูแห่งโชคลาภและความรัก";
    public string ScriptText
    {
        get => _scriptText;
        set => SetProperty(ref _scriptText, value);
    }

    private double _speed = 1.0;
    public double Speed { get => _speed; set => SetProperty(ref _speed, value); }

    private double _stability = 0.5;
    public double Stability { get => _stability; set => SetProperty(ref _stability, value); }

    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; set => SetProperty(ref _isGenerating, value); }

    private bool _isWriting;
    public bool IsWriting { get => _isWriting; set => SetProperty(ref _isWriting, value); }

    private string _scriptBrief = "ดวงประจำวันสำหรับลัคนาเมษ — เรื่องโชคลาภ + ความรัก";
    /// <summary>One-line topic the LLM expands into a full script.</summary>
    public string ScriptBrief
    {
        get => _scriptBrief;
        set => SetProperty(ref _scriptBrief, value);
    }

    public string ActiveLlmLabel
    {
        get
        {
            var p = _ctx.Providers.Llm.FirstOrDefault(x => x.Id == _ctx.Settings.ActiveLlm);
            return p?.DisplayName ?? _ctx.Settings.ActiveLlm;
        }
    }

    private string? _toastMessage;
    public string? ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }

    private string _toastKind = "info";
    public string ToastKind { get => _toastKind; set => SetProperty(ref _toastKind, value); }

    public bool HasTakes => Takes.Count > 0;
    public bool HasMusicTakes => MusicTakes.Count > 0;

    public IAsyncRelayCommand GenerateCommand { get; }
    public IAsyncRelayCommand GenerateMusicCommand { get; }
    public IAsyncRelayCommand WriteScriptCommand { get; }
    public IRelayCommand RefreshTakesCommand { get; }
    public IRelayCommand<VoiceTake> PlayTakeCommand { get; }
    public IRelayCommand<VoiceTake> RevealTakeCommand { get; }

    private VoiceTake? _currentlyPlaying;
    /// <summary>The take currently loaded in the inline MediaElement
    /// transport above the takes list. Setting this property is the
    /// signal to VoiceView.xaml.cs to swap the MediaElement source and
    /// start playback. Null = transport hidden. (T54 · 7.20)</summary>
    public VoiceTake? CurrentlyPlaying
    {
        get => _currentlyPlaying;
        set
        {
            if (SetProperty(ref _currentlyPlaying, value))
                OnPropertyChanged(nameof(HasCurrentlyPlaying));
        }
    }
    public bool HasCurrentlyPlaying => _currentlyPlaying is not null;

    public IRelayCommand StopPlaybackCommand { get; private set; } = null!;
    public IRelayCommand<VoiceTake> CopyPathCommand { get; }
    public IRelayCommand<VoiceTake> DeleteTakeCommand { get; }
    public IRelayCommand SwitchToTtsCommand { get; }
    public IRelayCommand SwitchToMusicCommand { get; }

    public VoiceViewModel(StudioContext ctx)
    {
        _ctx = ctx;

        foreach (var p in ctx.Providers.Voice)
            Providers.Add(new VoiceProviderOption { Provider = p });
        foreach (var p in ctx.Providers.Music)
            MusicProviders.Add(new MusicProviderOption { Provider = p });

        // Default to whichever provider has its key set, otherwise the first.
        _selectedProvider = Providers.FirstOrDefault(o => ctx.Settings.HasApiKey(o.Id))
                          ?? Providers.FirstOrDefault();
        _selectedMusicProvider = MusicProviders.FirstOrDefault();
        // Restore the persisted custom voice id (set by user in a previous session).
        _customVoiceId = ctx.Settings.GetSetting("elevenlabs_custom_voice") ?? "";
        ReloadVoices();

        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
        GenerateMusicCommand = new AsyncRelayCommand(GenerateMusicAsync);
        WriteScriptCommand = new AsyncRelayCommand(WriteScriptAsync);
        RefreshTakesCommand = new RelayCommand(RefreshTakes);
        PlayTakeCommand = new RelayCommand<VoiceTake>(PlayTake);
        RevealTakeCommand = new RelayCommand<VoiceTake>(RevealTake);
        StopPlaybackCommand = new RelayCommand(() => CurrentlyPlaying = null);
        CopyPathCommand = new RelayCommand<VoiceTake>(CopyPath);
        DeleteTakeCommand = new AsyncRelayCommand<VoiceTake>(DeleteTakeAsync);
        SwitchToTtsCommand = new RelayCommand(() => Mode = VoiceMode.Tts);
        SwitchToMusicCommand = new RelayCommand(() => Mode = VoiceMode.Music);

        RefreshTakes();
    }

    /// <summary>
    /// Re-read the account's voices — the view calls this on every visit, so
    /// a key pasted in Settings since the last one takes effect.
    /// </summary>
    public void RefreshVoiceList()
    {
        SyncStabilityStep();   // the model chip may have changed in Settings
        if (_selectedProvider?.Id == "elevenlabs") _ = LoadLiveVoicesAsync(_selectedProvider);
    }

    private void ReloadVoices()
    {
        Voices.Clear();
        VoicesNote = "";
        if (_selectedProvider is null) return;
        foreach (var v in _selectedProvider.Provider.AvailableVoices) Voices.Add(v);
        SelectedVoice = Voices.FirstOrDefault();
        if (_selectedProvider.Id == "elevenlabs") _ = LoadLiveVoicesAsync(_selectedProvider);
    }

    /// <summary>
    /// Swap in the voices the ElevenLabs key can really use. The built-in
    /// six are the Default set, which newer accounts never had.
    /// </summary>
    private async Task LoadLiveVoicesAsync(VoiceProviderOption option)
    {
        var key = _ctx.Settings[option.Id];
        if (string.IsNullOrWhiteSpace(key))
        {
            VoicesNote = "รายชื่อตั้งต้น — ใส่ API key ในหน้า Settings แล้วแอปจะดึงเสียงจากบัญชีของคุณมาแสดง";
            return;
        }
        VoicesNote = "กำลังโหลดรายชื่อเสียงจากบัญชี ElevenLabs…";
        try
        {
            var live = await option.Provider.ListVoicesAsync(key);
            // The user may have switched provider while the list was loading.
            if (!ReferenceEquals(_selectedProvider, option)) return;
            if (live.Count == 0)
            {
                VoicesNote = "บัญชีนี้ยังไม่มีเสียง — เพิ่มจาก Voice Library บนเว็บ ElevenLabs หรือวาง voice_id ด้านล่าง (รายชื่อที่เห็นคือเสียงตั้งต้นซึ่งบัญชีที่สมัครหลัง มี.ค. 2026 ใช้ไม่ได้)";
                return;
            }
            var keep = _selectedVoice?.Id;
            Voices.Clear();
            foreach (var v in live) Voices.Add(v);
            SelectedVoice = Voices.FirstOrDefault(v => v.Id == keep) ?? Voices.FirstOrDefault();
            VoicesNote = "";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_selectedProvider, option)) return;
            VoicesNote = "โหลดรายชื่อเสียงจากบัญชีไม่ได้ (" + ex.Message + ") — แสดงเสียงตั้งต้นแทน ซึ่งอาจใช้ไม่ได้กับบัญชีใหม่";
        }
    }

    public void RefreshTakes()
    {
        Takes.Clear();
        foreach (var t in _ctx.VoiceService.ListTakes()) Takes.Add(t);
        OnPropertyChanged(nameof(HasTakes));

        MusicTakes.Clear();
        foreach (var t in _ctx.VoiceService.ListMusicTakes()) MusicTakes.Add(t);
        OnPropertyChanged(nameof(HasMusicTakes));
    }

    private async Task GenerateAsync()
    {
        if (SelectedProvider is null)
        {
            ShowToast("เลือกผู้ให้บริการเสียงก่อน", "warn");
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptText))
        {
            ShowToast("พิมพ์บทที่จะให้อ่านก่อน", "warn");
            return;
        }
        // ElevenLabs accepts a cloned/professional voice id pasted into the
        // custom box — overrides the default-library dropdown when present.
        // The pasted voice_id is an ElevenLabs id; OpenAI answered it with a 400.
        var voiceId = HasCustomVoice && SelectedProvider.Id == "elevenlabs" ? _customVoiceId.Trim() : SelectedVoice?.Id;
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            ShowToast("เลือกเสียงจากรายการ หรือวาง voice_id ของเสียงที่โคลนไว้", "warn");
            return;
        }

        IsGenerating = true;
        ShowToast($"กำลังสร้างเสียงผ่าน {SelectedProvider.DisplayName}…", "info");
        try
        {
            var take = await _ctx.VoiceService.GenerateAsync(
                SelectedProvider.Id, voiceId, ScriptText, Speed, Stability);
            Takes.Insert(0, take);
            OnPropertyChanged(nameof(HasTakes));
            ShowToast($"เสียงพร้อมแล้ว · {take.FileName}", "ok");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, "err");
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private async Task GenerateMusicAsync()
    {
        if (SelectedMusicProvider is null)
        {
            ShowToast("ไม่มีช่องทางสร้างเพลงให้เลือก", "warn");
            return;
        }
        if (string.IsNullOrWhiteSpace(MusicPrompt))
        {
            ShowToast("บรรยายเพลงที่ต้องการก่อน", "warn");
            return;
        }

        IsGenerating = true;
        ShowToast($"กำลังสร้างเพลงผ่าน {SelectedMusicProvider.DisplayName}…", "info");

        // Renting a card for music runs the same 10-40 minute warm-up as a
        // render. Without a live stage line the user sees a spinner, assumes it
        // hung, cancels, and pays for the download twice.
        var warmup = new Progress<Services.Gpu.GpuWarmupProgress>(p => MusicStage = p.Message);
        try
        {
            var take = await _ctx.VoiceService.GenerateMusicAsync(
                SelectedMusicProvider.Id, MusicModel, MusicPrompt, MusicDuration,
                default, IsAceStepRoute ? MusicLyrics : null, warmup);
            MusicTakes.Insert(0, take);
            OnPropertyChanged(nameof(HasMusicTakes));
            ShowToast($"เพลงพร้อมแล้ว · {take.FileName}", "ok");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, "err");
        }
        finally
        {
            IsGenerating = false;
            MusicStage = "";
        }
    }

    private async Task WriteScriptAsync()
    {
        if (string.IsNullOrWhiteSpace(ScriptBrief))
        {
            ShowToast("พิมพ์โจทย์สั้น ๆ ก่อน (หัวข้อ กลุ่มผู้ชม อารมณ์)", "warn");
            return;
        }

        IsWriting = true;
        ShowToast($"กำลังเขียนบทผ่าน {ActiveLlmLabel}…", "info");
        try
        {
            var text = await _ctx.Llm.WriteFortuneScriptAsync(ScriptBrief);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("LLM ตอบกลับมาว่างเปล่า — ตรวจ key และรุ่นในหน้า Settings", "err");
                return;
            }
            ScriptText = text.Trim();
            ShowToast($"บทพร้อมแล้ว · {ActiveLlmLabel}", "ok");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, "err");
        }
        finally
        {
            IsWriting = false;
        }
    }

    private void PlayTake(VoiceTake? take)
    {
        if (take is null) return;
        if (!System.IO.File.Exists(take.FilePath))
        {
            ShowToast("ไม่พบไฟล์เสียงนี้บนดิสก์แล้ว", "err");
            return;
        }
        // In-place inline playback (T54 / 7.20) — code-behind picks up the
        // CurrentlyPlaying change and swaps the MediaElement source. Fall
        // back to shell-open only if the file's extension isn't one that
        // WPF's MediaElement decodes (rare for mp3/wav from the providers).
        CurrentlyPlaying = take;
    }

    private void RevealTake(VoiceTake? take)
    {
        if (take is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{take.FilePath}\""));
        }
        catch { /* ignore */ }
    }

    private void CopyPath(VoiceTake? take)
    {
        if (take is null) return;
        try
        {
            System.Windows.Clipboard.SetText(take.FilePath);
            ShowToast("คัดลอกตำแหน่งไฟล์แล้ว — วางในช่องเลือกเสียงของ Render Film ได้เลย", "ok");
        }
        catch (Exception ex) { ShowToast($"คัดลอกไม่สำเร็จ: {ex.Message}", "err"); }
    }

    private async Task DeleteTakeAsync(VoiceTake? take)
    {
        if (take is null) return;
        var answer = System.Windows.MessageBox.Show(
            $"ลบไฟล์ {take.FileName} ถาวร?\nกู้คืนไม่ได้",
            "ลบไฟล์เสียง", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        // The inline player holds the file open; release it first or the
        // delete fails and the row vanished anyway. The player lets go of the
        // handle a moment later, hence the short retries.
        // By path: RefreshTakes builds new objects, so the playing take is
        // usually not the same instance as the row being deleted.
        if (CurrentlyPlaying is { } playing
            && string.Equals(playing.FilePath, take.FilePath, StringComparison.OrdinalIgnoreCase))
            CurrentlyPlaying = null;
        var deleted = false;
        for (var attempt = 0; attempt < 5 && !deleted; attempt++)
        {
            if (attempt > 0) await Task.Delay(250);
            deleted = _ctx.VoiceService.DeleteTake(take);
        }
        if (!deleted)
        {
            ShowToast($"ลบ {take.FileName} ไม่ได้ — ไฟล์อาจถูกเปิดอยู่ในโปรแกรมอื่น", "err");
            return;
        }
        Takes.Remove(take);
        MusicTakes.Remove(take);
        OnPropertyChanged(nameof(HasTakes));
        OnPropertyChanged(nameof(HasMusicTakes));
        ShowToast($"ลบ {take.FileName} แล้ว", "ok");
    }

    private async void ShowToast(string message, string kind)
    {
        ToastMessage = message;
        ToastKind = kind;
        try
        {
            // An error needs time to be read; a "working…" line stays until
            // the result replaces it.
            await Task.Delay(kind switch { "err" => 12000, "warn" => 6000, "info" => 60000, _ => 3500 });
            if (ToastMessage == message) ToastMessage = null;
        }
        catch { }
    }
}
