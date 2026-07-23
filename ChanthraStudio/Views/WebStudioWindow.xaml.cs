using System.Windows;

namespace ChanthraStudio.Views;

/// <summary>
/// Opaque top-level window that hosts the embedded WebView2 (the main window is
/// transparent, where WebView2 can't render). Single-instance: opening again
/// just focuses the existing window.
/// </summary>
public partial class WebStudioWindow : Window
{
    private static WebStudioWindow? _instance;

    public WebStudioWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (ReferenceEquals(_instance, this)) _instance = null; };
    }

    /// <summary>Open the Web Studio window, or focus it if already open.</summary>
    public static void ShowSingleton()
    {
        if (_instance is null)
        {
            _instance = new WebStudioWindow();
            _instance.Show();
            return;
        }

        if (_instance.WindowState == WindowState.Minimized)
            _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }
}
