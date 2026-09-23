using System.Windows;
using System.Windows.Controls;
using ChanthraStudio.Models;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Lazy DataContext: design-time uses the parameterless ctor's
            // seeded data, runtime hooks up the real repository.
            //
            // This used to test `vm.Schedules.Count == 0`, which the design-time
            // ViewModel's one demo row made false — so the live ViewModel was
            // never built. The page showed a fake schedule, Add/Save/Run now did
            // nothing, and toggling the demo card (Id = 1) wrote it over the real
            // row 1. Identity, not emptiness, is the right test.
            if (DataContext is not ScheduleViewModel { IsLive: true })
            {
                var s = App.Current?.Studio;
                if (s is not null)
                {
                    (DataContext as ScheduleViewModel)?.Dispose();
                    DataContext = new ScheduleViewModel(s);
                }
            }
        };
        // The view is reused across visits; only stop listening for fires
        // while it is off screen, and rebuild the listener on return.
        Unloaded += (_, _) => (DataContext as ScheduleViewModel)?.Detach();
        Loaded += (_, _) => (DataContext as ScheduleViewModel)?.Reattach();
    }

    private void ScheduleCard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScheduleViewModel vm) return;
        if (sender is FrameworkElement fe && fe.DataContext is Schedule s)
            vm.Selected = s;
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        // The ToggleButton's IsChecked is already two-way bound, so we only
        // need to persist on click. Wrapping in ToggleEnabledCommand keeps
        // the "recompute next fire on re-enable" logic in one place.
        if (DataContext is not ScheduleViewModel vm) return;
        if (sender is FrameworkElement fe && fe.DataContext is Schedule s)
        {
            // The binding has already flipped IsEnabled; the ViewModel
            // decides what else that means (a stale next-fire must be moved
            // forward, or re-enabling fires it within the minute).
            vm.ApplyEnabledChange(s);
        }
        // Don't bubble — otherwise ScheduleCard_Click fires too.
        e.Handled = true;
    }

    private void KindDaily_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScheduleViewModel vm && vm.Selected is { } s)
            s.Kind = ScheduleKind.DailySlots;
    }

    private void KindInterval_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScheduleViewModel vm && vm.Selected is { } s)
            s.Kind = ScheduleKind.Interval;
    }

    private void RouteComfy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScheduleViewModel vm && vm.Selected is { } s)
            s.Route = "comfyui";
    }

    private void RouteReplicate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScheduleViewModel vm && vm.Selected is { } s)
            s.Route = "replicate";
    }
}
