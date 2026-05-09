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
            if (DataContext is not ScheduleViewModel vm || vm.Schedules.Count == 0)
            {
                var s = App.Current?.Studio;
                if (s is not null) DataContext = new ScheduleViewModel(s);
            }
        };
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
            // Save the current value (already toggled by the binding).
            App.Current?.Studio?.Schedules.Update(s);
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
