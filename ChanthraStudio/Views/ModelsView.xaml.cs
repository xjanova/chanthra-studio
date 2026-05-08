using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();
        // Same lazy DataContext pattern as SettingsView — design-time
        // gets the parameterless constructor's seeded data, runtime gets
        // a real ViewModel hooked up to the live ComfyUI client.
        Loaded += (_, _) =>
        {
            if (DataContext is not ModelsViewModel vm || vm.Groups.Count == 0)
            {
                var s = App.Current?.Studio;
                if (s is not null) DataContext = new ModelsViewModel(s);
            }
        };
    }
}
