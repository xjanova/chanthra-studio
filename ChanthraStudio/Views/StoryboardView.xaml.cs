using System.Windows.Controls;

namespace ChanthraStudio.Views;

/// <summary>
/// Storyboard studio view. The DataContext is supplied by the shell
/// (MainWindow binds it to MainViewModel.Board) so the generated board
/// survives navigating away and back — unlike the SeedanceWizardView, which
/// is a stateless prompt authoring tool and mints its own VM.
/// </summary>
public partial class StoryboardView : UserControl
{
    public StoryboardView()
    {
        InitializeComponent();
    }
}
