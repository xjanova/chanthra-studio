using System.Windows;
using ChanthraStudio.Services;

namespace ChanthraStudio;

public partial class App : Application
{
    public new static App Current => (App)Application.Current;

    public StudioContext Studio { get; } = new();

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Studio.Settings.Save();
        }
        catch
        {
            // Best-effort save on exit — don't block the shutdown if disk is full.
        }
        base.OnExit(e);
    }
}
