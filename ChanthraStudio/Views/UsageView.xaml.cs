using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class UsageView : UserControl
{
    public UsageView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not UsageViewModel vm || vm.Buckets.Count <= 4 && vm.RecentEvents.Count == 0)
            {
                var s = App.Current?.Studio;
                if (s is not null) DataContext = new UsageViewModel(s);
            }
        };
    }
}
