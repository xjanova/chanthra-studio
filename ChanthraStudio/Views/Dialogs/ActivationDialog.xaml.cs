using System.Windows;
using System.Windows.Controls;

namespace ChanthraStudio.Views.Dialogs;

public partial class ActivationDialog : Window
{
    public ActivationDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
