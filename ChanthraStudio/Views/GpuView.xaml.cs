using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class GpuView : UserControl
{
    public GpuView()
    {
        InitializeComponent();

        // The XAML instantiates a design-time VM with no StudioContext so the
        // designer never touches SQLite. Build a live one each time the panel
        // is shown, and tear it down when it's hidden — the VM runs a
        // 5-second cost ticker, and leaving those stacked up behind
        // navigation would burn CPU for a page nobody is looking at.
        Loaded += (_, _) =>
        {
            var studio = App.Current?.Studio;
            if (studio is null) return;
            if (DataContext is GpuViewModel old) old.Dispose();
            DataContext = new GpuViewModel(studio);
        };

        Unloaded += (_, _) =>
        {
            if (DataContext is GpuViewModel vm) vm.Dispose();
        };
    }
}
