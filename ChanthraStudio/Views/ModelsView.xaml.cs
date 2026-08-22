using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();

        // The XAML declares a design-time ViewModel so the designer never
        // touches SQLite or the network. Swap it for a live one on load.
        //
        // This used to be guarded by `vm.Groups.Count == 0`, which never held:
        // the design-time constructor seeds six demo groups, so the guard was
        // always false and the live ViewModel was never built. The page has
        // been showing hard-coded placeholder model names — "RTX 4090",
        // "juggernautXL_v9" — on every machine, whatever ComfyUI actually had
        // installed. Identity, not emptiness, is the right test.
        Loaded += (_, _) =>
        {
            var studio = App.Current?.Studio;
            if (studio is not null && DataContext is not ModelsViewModel { IsLive: true })
            {
                (DataContext as ModelsViewModel)?.Dispose();
                DataContext = new ModelsViewModel(studio);
            }

            // A ScrollViewer scrolls to whichever descendant takes keyboard
            // focus, and the first focusable control here is two cards down —
            // so the panel opened with the server status and workflow picker
            // already scrolled off the top, which reads as "they're missing".
            Dispatcher.BeginInvoke(new System.Action(() => SettingsScroller.ScrollToTop()),
                                   System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }
}
