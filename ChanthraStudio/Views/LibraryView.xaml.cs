using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // The ViewSwitcher hosts this view with no explicit DataContext, so
            // it INHERITS MainViewModel from the ContentControl — a plain
            // `is null` guard never fires and the view would bind to the wrong
            // VM. Force our own VM unless one of the right type is already set.
            if (DataContext is not LibraryViewModel lvm)
                DataContext = new LibraryViewModel(App.Current.Studio);
            else
                lvm.Refresh();
        };
        Unloaded += (_, _) => (DataContext as System.IDisposable)?.Dispose();
    }
}
