using System.Windows;
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

    /// <summary>
    /// "Quick pick" model chips below the model textbox — clicking one
    /// drops its slug into MusicModel without the user having to type.
    /// Tag carries the slug ("meta/musicgen", etc.).
    /// </summary>
    private void MusicQuickPick_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VoiceViewModel vm) return;
        if (sender is RadioButton rb && rb.Tag is string slug)
            vm.MusicModel = slug;
    }
}
