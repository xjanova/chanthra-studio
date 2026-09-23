using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public enum AppView { Generate, Seedance, Storyboard, Edit, Voice, Flow, WebStudio, Library, Models, Queue, Schedule, Gpu, Usage, Settings }

public sealed class MainViewModel : ObservableObject
{
    private AppView _activeView = AppView.Generate;
    public AppView ActiveView
    {
        get => _activeView;
        set => SetProperty(ref _activeView, value);
    }

    /// <summary>
    /// Title-bar search box → SearchBus on the StudioContext. Any list view
    /// that opted in (Library, Generate storyboard) re-filters live as the
    /// user types.
    /// </summary>
    public string SearchQuery
    {
        get => (System.Windows.Application.Current as App)?.Studio.Search.Query ?? "";
        set
        {
            var bus = (System.Windows.Application.Current as App)?.Studio.Search;
            if (bus is null) return;
            if (bus.Query == value) return;
            bus.Query = value;
            OnPropertyChanged();
        }
    }


    public GenerateViewModel Generate { get; }
    public StoryboardViewModel Board { get; }
    public WebStudioViewModel WebStudio { get; }
    public StatusBarViewModel Status { get; } = new();

    public IRelayCommand<string> SwitchViewCommand { get; }

    public MainViewModel()
    {
        // App.Current is null at design-time — use a parameterless GenerateVM there
        // so the XAML designer doesn't bootstrap SQLite + the provider registry.
        var studio = (System.Windows.Application.Current as App)?.Studio;
        Generate = studio is not null ? new GenerateViewModel(studio) : new GenerateViewModel();
        Board = studio is not null ? new StoryboardViewModel(studio) : new StoryboardViewModel();
        WebStudio = studio is not null ? new WebStudioViewModel(studio) : new WebStudioViewModel();

        // Kick the status-bar autosave label ticker so it stops showing
        // the stale "Auto-save —" placeholder forever.
        Status.StartAutosaveTicker();

        SwitchViewCommand = new RelayCommand<string>(view =>
        {
            if (view is not null && System.Enum.TryParse<AppView>(view, ignoreCase: true, out var v))
                ActiveView = v;
        });

    }
}
