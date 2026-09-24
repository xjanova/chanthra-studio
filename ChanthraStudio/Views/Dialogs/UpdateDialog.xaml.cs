using System.Windows;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views.Dialogs;

public partial class UpdateDialog : Window
{
    public UpdateDialog(UpdateViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Closing by any route — a button or the X — abandons the download.
        Closing += (_, _) => vm.CancelPending();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
