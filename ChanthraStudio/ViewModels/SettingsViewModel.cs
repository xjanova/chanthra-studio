using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class ProviderRow : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IProvider _provider;

    public string Id => _provider.Id;
    public string DisplayName => _provider.DisplayName;
    public string ApiKeyHint => _provider.ApiKeyHint;
    public bool RequiresApiKey => _provider.RequiresApiKey;
    public ProviderKind Kind => _provider.Kind;

    private string _apiKeyDraft;
    public string ApiKeyDraft
    {
        get => _apiKeyDraft;
        set
        {
            if (SetProperty(ref _apiKeyDraft, value))
                IsDirty = true;
        }
    }

    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set => SetProperty(ref _isDirty, value); }

    private string _statusLabel = "";
    public string StatusLabel { get => _statusLabel; set => SetProperty(ref _statusLabel, value); }

    /// <summary>"" | ok | warn | err | probing | saved</summary>
    private string _statusKind = "";
    public string StatusKind { get => _statusKind; set => SetProperty(ref _statusKind, value); }

    public IAsyncRelayCommand TestCommand { get; }
    public IRelayCommand SaveCommand { get; }

    public ProviderRow(IProvider provider, AppSettings settings)
    {
        _provider = provider;
        _settings = settings;
        _apiKeyDraft = settings[provider.Id];
        TestCommand = new AsyncRelayCommand(TestAsync);
        SaveCommand = new RelayCommand(Save);
        UpdateStatusFromStorage();
    }

    private void UpdateStatusFromStorage()
    {
        if (!RequiresApiKey)
        {
            StatusLabel = "no key needed";
            StatusKind = "ok";
            return;
        }
        if (_settings.HasApiKey(Id))
        {
            StatusLabel = "key saved";
            StatusKind = "ok";
        }
        else
        {
            StatusLabel = "no key";
            StatusKind = "warn";
        }
    }

    private async Task TestAsync()
    {
        StatusLabel = "probing…";
        StatusKind = "probing";
        var key = string.IsNullOrEmpty(ApiKeyDraft) ? _settings[Id] : ApiKeyDraft;
        var health = await _provider.ProbeAsync(key);
        StatusLabel = health.Status;
        StatusKind = health.Ok ? "ok" : "err";
    }

    private async void Save()
    {
        _settings.SetApiKey(Id, ApiKeyDraft);
        try
        {
            _settings.Save();
            IsDirty = false;
            StatusLabel = "saved ✓";
            StatusKind = "saved";
            // Hold the green flash for a beat, then revert to "key saved".
            try { await Task.Delay(2500); } catch { }
            UpdateStatusFromStorage();
        }
        catch (Exception ex)
        {
            StatusLabel = "save failed";
            StatusKind = "err";
            // best-effort surface; full diagnosis lands when we add ILogger.
            System.Diagnostics.Debug.WriteLine($"settings save failed: {ex}");
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ProviderRegistry _registry;

    public ObservableCollection<ProviderRow> LlmProviders { get; } = new();
    public ObservableCollection<ProviderRow> VideoProviders { get; } = new();
    public ObservableCollection<ProviderRow> VoiceProviders { get; } = new();
    public ObservableCollection<ProviderRow> PostingProviders { get; } = new();

    private string? _toastMessage;
    public string? ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }

    /// <summary>"" | ok | warn | err | info</summary>
    private string _toastKind = "info";
    public string ToastKind { get => _toastKind; set => SetProperty(ref _toastKind, value); }

    public string ActiveLlm
    {
        get => _settings.ActiveLlm;
        set
        {
            if (_settings.ActiveLlm == value) return;
            _settings.ActiveLlm = value;
            OnPropertyChanged();
        }
    }

    public string ActiveVideo
    {
        get => _settings.ActiveVideo;
        set
        {
            if (_settings.ActiveVideo == value) return;
            _settings.ActiveVideo = value;
            OnPropertyChanged();
        }
    }

    public string PostFacebookPageId
    {
        get => _settings.PostFacebookPageId;
        set
        {
            if (_settings.PostFacebookPageId == value) return;
            _settings.PostFacebookPageId = value;
            OnPropertyChanged();
            // The api-key rows persist on their per-row Save button, but the
            // Page ID + Webhook URL textboxes had no equivalent — losing the
            // value on hard kill. Persist on every keystroke (debounced by
            // SQLite's UPSERT being effectively free).
            TryPersist();
        }
    }

    public string PostWebhookUrl
    {
        get => _settings.PostWebhookUrl;
        set
        {
            if (_settings.PostWebhookUrl == value) return;
            _settings.PostWebhookUrl = value;
            OnPropertyChanged();
            TryPersist();
        }
    }

    public string ComfyUiUrl
    {
        get => _settings.ComfyUiUrl;
        set
        {
            if (_settings.ComfyUiUrl == value) return;
            _settings.ComfyUiUrl = value;
            OnPropertyChanged();
            TryPersist();
        }
    }

    private void TryPersist()
    {
        try { _settings.Save(); }
        catch { /* DB write failures are non-fatal — OnExit retries. */ }
    }

    public string DataPath => AppPaths.Root;
    public string DatabasePath => AppPaths.DatabaseFile;
    public string SettingsStorageHint => $"SQLite · {AppPaths.DatabaseFile}";

    public IRelayCommand SaveAllCommand { get; }
    public IRelayCommand RevealDataFolderCommand { get; }
    public IRelayCommand RevealWorkflowsFolderCommand { get; }

    public SettingsViewModel(AppSettings settings, ProviderRegistry registry)
    {
        _settings = settings;
        _registry = registry;

        foreach (var p in registry.Llm) LlmProviders.Add(new ProviderRow(p, settings));
        foreach (var p in registry.Video) VideoProviders.Add(new ProviderRow(p, settings));
        foreach (var p in registry.Voice) VoiceProviders.Add(new ProviderRow(p, settings));
        foreach (var p in registry.Posting) PostingProviders.Add(new ProviderRow(p, settings));

        SaveAllCommand = new RelayCommand(SaveAll);

        RevealDataFolderCommand = new RelayCommand(() => RevealFolder(AppPaths.Root));
        RevealWorkflowsFolderCommand = new RelayCommand(() =>
        {
            // The workflows folder lives next to the .exe (built-ins) AND under
            // the user data folder (drop-ins). We open the user folder, since
            // that's the one users actually edit.
            var userWorkflows = System.IO.Path.Combine(AppPaths.Root, "workflows");
            try { System.IO.Directory.CreateDirectory(userWorkflows); } catch { }
            RevealFolder(userWorkflows);
        });
    }

    private static void RevealFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore — path is shown on the page.
        }
    }

    private void SaveAll()
    {
        try
        {
            // Per-row Save mutates the in-memory dict; final Save below
            // persists everything in one transaction.
            foreach (var row in LlmProviders.Concat(VideoProviders).Concat(VoiceProviders).Concat(PostingProviders))
            {
                if (row.IsDirty) row.SaveCommand.Execute(null);
            }
            _settings.Save();
            ShowToast("All settings saved · written to SQLite", "ok");
        }
        catch (Exception ex)
        {
            ShowToast("Save failed: " + ex.Message, "err");
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
