using System.Windows;
using System.Windows.Controls;

namespace ChanthraStudio.Controls;

public partial class TitleBar : UserControl
{
    public TitleBar() => InitializeComponent();

    private void MinBtn_Click(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w is not null) w.WindowState = WindowState.Minimized;
    }

    private void MaxBtn_Click(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w is null) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Window.GetWindow(this)?.Close();
}
