using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Force our own VM — the ViewSwitcher leaves DataContext inheriting
            // MainViewModel, so a plain `is null` guard never fires.
            if (DataContext is not QueueViewModel qvm)
                DataContext = new QueueViewModel(App.Current.Studio);
            else
                qvm.Refresh();
        };
        Unloaded += (_, _) => (DataContext as System.IDisposable)?.Dispose();
    }
}
