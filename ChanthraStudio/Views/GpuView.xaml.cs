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
            var vm = new GpuViewModel(studio);
            DataContext = vm;
            // PasswordBox.Password cannot be bound; prime the boxes by hand.
            _priming = true;
            ApiKeyBox.Password = vm.ApiKeyDraft;
            HfTokenBox.Password = vm.HfTokenDraft;
            _priming = false;
        };

        Unloaded += (_, _) =>
        {
            if (DataContext is GpuViewModel vm) vm.Dispose();
        };
    }

    private bool _priming;

    private void Secret_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_priming || DataContext is not GpuViewModel vm) return;
        if (ReferenceEquals(sender, ApiKeyBox)) vm.ApiKeyDraft = ApiKeyBox.Password;
        else if (ReferenceEquals(sender, HfTokenBox)) vm.HfTokenDraft = HfTokenBox.Password;
    }
}
