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
}

public sealed class MusicProviderOption
{
    public IMusicProvider Provider { get; init; } = null!;
    public string Id => Provider.Id;
    public string DisplayName => Provider.DisplayName;
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
        }
    }

    private VoicePreset? _selectedVoice;
    public VoicePreset? SelectedVoice
    {
        get => _selectedVoice;
        set => SetProperty(ref _selectedVoice, value);
    }

    private MusicProviderOption? _selectedMusicProvider;
    public MusicProviderOption? SelectedMusicProvider
    {
        get => _selectedMusicProvider;
        set => SetProperty(ref _selectedMusicProvider, value);
    }

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
        set => SetProperty(ref _musicDuration, Math.Clamp(value, 4, 120));
    }

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
        ReloadVoices();

        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
        GenerateMusicCommand = new AsyncRelayCommand(GenerateMusicAsync);
        WriteScriptCommand = new AsyncRelayCommand(WriteScriptAsync);
        RefreshTakesCommand = new RelayCommand(RefreshTakes);
        PlayTakeCommand = new RelayCommand<VoiceTake>(PlayTake);
        RevealTakeCommand = new RelayCommand<VoiceTake>(RevealTake);
        CopyPathCommand = new RelayCommand<VoiceTake>(CopyPath);
        DeleteTakeCommand = new RelayCommand<VoiceTake>(DeleteTake);
        SwitchToTtsCommand = new RelayCommand(() => Mode = VoiceMode.Tts);
        SwitchToMusicCommand = new RelayCommand(() => Mode = VoiceMode.Music);

        RefreshTakes();
    }

    private void ReloadVoices()
    {
        Voices.Clear();
        if (_selectedProvider is null) return;
        foreach (var v in _selectedProvider.Provider.AvailableVoices) Voices.Add(v);
        SelectedVoice = Voices.FirstOrDefault();
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
        if (SelectedProvider is null || SelectedVoice is null)
        {
            ShowToast("Pick a provider + voice first.", "warn");
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptText))
        {
            ShowToast("Type something to voice.", "warn");
            return;
        }

        IsGenerating = true;
        ShowToast($"Synthesising via {SelectedProvider.DisplayName}…", "info");
        try
        {
            var take = await _ctx.VoiceService.GenerateAsync(
                SelectedProvider.Id, SelectedVoice.Id, ScriptText, Speed, Stability);
            Takes.Insert(0, take);
            OnPropertyChanged(nameof(HasTakes));
            ShowToast($"Voice ready · {take.FileName}", "ok");
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
            ShowToast("No music provider available.", "warn");
            return;
        }
        if (string.IsNullOrWhiteSpace(MusicPrompt))
        {
            ShowToast("Describe the music you want first.", "warn");
            return;
        }

        IsGenerating = true;
        ShowToast($"Generating music via {SelectedMusicProvider.DisplayName}…", "info");
        try
        {
            var take = await _ctx.VoiceService.GenerateMusicAsync(
                SelectedMusicProvider.Id, MusicModel, MusicPrompt, MusicDuration);
            MusicTakes.Insert(0, take);
            OnPropertyChanged(nameof(HasMusicTakes));
            ShowToast($"Music ready · {take.FileName}", "ok");
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

    private async Task WriteScriptAsync()
    {
        if (string.IsNullOrWhiteSpace(ScriptBrief))
        {
            ShowToast("Type a brief first (topic, audience, vibe).", "warn");
            return;
        }

        IsWriting = true;
        ShowToast($"Writing via {ActiveLlmLabel}…", "info");
        try
        {
            var text = await _ctx.Llm.WriteFortuneScriptAsync(ScriptBrief);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("LLM returned empty — check your key + model.", "err");
                return;
            }
            ScriptText = text.Trim();
            ShowToast($"Script ready · {ActiveLlmLabel}", "ok");
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
        try
        {
            Process.Start(new ProcessStartInfo { FileName = take.FilePath, UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast($"Open failed: {ex.Message}", "err"); }
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
            ShowToast("Path copied — paste into Render Film's audio picker.", "ok");
        }
        catch (Exception ex) { ShowToast($"Copy failed: {ex.Message}", "err"); }
    }

    private void DeleteTake(VoiceTake? take)
    {
        if (take is null) return;
        _ctx.VoiceService.DeleteTake(take);
        Takes.Remove(take);
        MusicTakes.Remove(take);
        OnPropertyChanged(nameof(HasTakes));
        OnPropertyChanged(nameof(HasMusicTakes));
        ShowToast($"Deleted {take.FileName}", "ok");
    }

    private async void ShowToast(string message, string kind)
    {
        ToastMessage = message;
        ToastKind = kind;
        try
        {
            await Task.Delay(3000);
            if (ToastMessage == message) ToastMessage = null;
        }
        catch { }
    }
}
