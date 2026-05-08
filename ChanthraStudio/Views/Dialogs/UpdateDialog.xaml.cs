using System.Windows;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views.Dialogs;

public partial class UpdateDialog : Window
{
    public UpdateDialog(UpdateViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
