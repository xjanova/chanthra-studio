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
            if (DataContext is null)
                DataContext = new LibraryViewModel(App.Current.Studio);
            else if (DataContext is LibraryViewModel lvm)
                lvm.Refresh();
        };
    }
}
