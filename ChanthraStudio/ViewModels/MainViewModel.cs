using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public enum AppView { Generate, Edit, Voice, Flow, Library, Models, Queue, Schedule, Usage, Settings }

public sealed class TabItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsLive { get; set; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

public sealed class MainViewModel : ObservableObject
{
    private AppView _activeView = AppView.Generate;
    public AppView ActiveView
    {
        get => _activeView;
        set => SetProperty(ref _activeView, value);
    }

    private string _activeTabId = "empress";
    public string ActiveTabId
    {
        get => _activeTabId;
        set
        {
            if (SetProperty(ref _activeTabId, value))
                RefreshActiveTab();
        }
    }

    public ObservableCollection<TabItem> OpenTabs { get; } = new();

    public GenerateViewModel Generate { get; }
    public StatusBarViewModel Status { get; } = new();

    public IRelayCommand<string> SwitchViewCommand { get; }
    public IRelayCommand<string> SwitchTabCommand { get; }
    public IRelayCommand NewTabCommand { get; }
    public IRelayCommand<string> CloseTabCommand { get; }

    public MainViewModel()
    {
        // App.Current is null at design-time — use a parameterless GenerateVM there
        // so the XAML designer doesn't bootstrap SQLite + the provider registry.
        var studio = (System.Windows.Application.Current as App)?.Studio;
        Generate = studio is not null ? new GenerateViewModel(studio) : new GenerateViewModel();

        OpenTabs.Add(new TabItem { Id = "empress", Title = "The Empress", IsLive = true });
        OpenTabs.Add(new TabItem { Id = "moondog", Title = "Moondog", IsLive = false });
        OpenTabs.Add(new TabItem { Id = "veil-fall", Title = "Veil Fall", IsLive = false });

        SwitchViewCommand = new RelayCommand<string>(view =>
        {
            if (view is not null && System.Enum.TryParse<AppView>(view, ignoreCase: true, out var v))
                ActiveView = v;
        });

        SwitchTabCommand = new RelayCommand<string>(id =>
        {
            if (id is not null) ActiveTabId = id;
        });

        NewTabCommand = new RelayCommand(() =>
        {
            var id = $"untitled-{OpenTabs.Count + 1}";
            OpenTabs.Add(new TabItem { Id = id, Title = "Untitled" });
            ActiveTabId = id;
        });

        CloseTabCommand = new RelayCommand<string>(id =>
        {
            if (id is null) return;
            var t = OpenTabs.FirstOrDefault(x => x.Id == id);
            if (t is null || OpenTabs.Count <= 1) return;
            var idx = OpenTabs.IndexOf(t);
            OpenTabs.Remove(t);
            if (ActiveTabId == id)
                ActiveTabId = OpenTabs[System.Math.Min(idx, OpenTabs.Count - 1)].Id;
        });

        RefreshActiveTab();
    }

    private void RefreshActiveTab()
    {
        foreach (var tab in OpenTabs)
            tab.IsActive = tab.Id == ActiveTabId;
    }
}
