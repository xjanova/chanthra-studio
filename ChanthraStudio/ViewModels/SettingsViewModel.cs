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

    private string _statusKind = "";   // "" | ok | warn | err | probing
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

    private void Save()
    {
        _settings.SetApiKey(Id, ApiKeyDraft);
        _settings.Save();
        IsDirty = false;
        UpdateStatusFromStorage();
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ProviderRegistry _registry;

    public ObservableCollection<ProviderRow> LlmProviders { get; } = new();
    public ObservableCollection<ProviderRow> VideoProviders { get; } = new();
    public ObservableCollection<ProviderRow> PostingProviders { get; } = new();

    public string ComfyUiUrl
    {
        get => _settings.ComfyUiUrl;
        set
        {
            if (_settings.ComfyUiUrl == value) return;
            _settings.ComfyUiUrl = value;
            OnPropertyChanged();
        }
    }

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
        }
    }

    public string DataPath => AppPaths.Root;
    public string DatabasePath => AppPaths.DatabaseFile;
    public string SettingsPath => AppPaths.SettingsFile;

    public IRelayCommand SaveAllCommand { get; }
    public IRelayCommand RevealDataFolderCommand { get; }

    public SettingsViewModel(AppSettings settings, ProviderRegistry registry)
    {
        _settings = settings;
        _registry = registry;

        foreach (var p in registry.Llm) LlmProviders.Add(new ProviderRow(p, settings));
        foreach (var p in registry.Video) VideoProviders.Add(new ProviderRow(p, settings));
        foreach (var p in registry.Posting) PostingProviders.Add(new ProviderRow(p, settings));

        SaveAllCommand = new RelayCommand(() =>
        {
            foreach (var row in LlmProviders.Concat(VideoProviders).Concat(PostingProviders))
                if (row.IsDirty) row.SaveCommand.Execute(null);
            _settings.Save();
        });

        RevealDataFolderCommand = new RelayCommand(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.Root,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // ignore — user can still see the path on the page.
            }
        });
    }
}
