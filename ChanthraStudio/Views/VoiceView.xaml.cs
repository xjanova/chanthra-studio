using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class VoiceView : UserControl
{
    public VoiceView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is null)
                DataContext = new VoiceViewModel(App.Current.Studio);
            else if (DataContext is VoiceViewModel vvm)
                vvm.RefreshTakes();
        };
    }
}
