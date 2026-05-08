using System.Windows;
using ChanthraStudio.Helpers;

namespace ChanthraStudio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.HookMaxBounds(this);
    }
}
