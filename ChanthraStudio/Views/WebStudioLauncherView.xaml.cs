using System.Windows;
using System.Windows.Controls;

namespace ChanthraStudio.Views;

/// <summary>
/// Shown in the Web Studio sidebar tab. The browser itself lives in a separate
/// opaque window (WebView2 can't render in the transparent main window), so
/// this panel just opens / focuses that window — automatically on first show,
/// and via the button afterwards.
/// </summary>
public partial class WebStudioLauncherView : UserControl
{
    public WebStudioLauncherView()
    {
        InitializeComponent();
        Loaded += (_, _) => WebStudioWindow.ShowSingleton();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => WebStudioWindow.ShowSingleton();
}
