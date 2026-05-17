using System.Windows.Controls;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is null)
                DataContext = new EditorViewModel(App.Current.Studio);
        };
    }

    private void Fps_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (int.TryParse(tag, out var fps)) vm.Fps = fps;
    }

    private void AudioTake_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        if (sender is not ComboBox cb) return;
        if (cb.SelectedItem is VoiceTake t) vm.AudioPath = t.FilePath;
    }
}
