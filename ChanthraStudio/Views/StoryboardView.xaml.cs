using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;

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

    // The reference rows have always said "drag image or click"; only the
    // click was wired.

    private static readonly string[] RefExt = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    // Stills the engines take as a reference: no GIF, TIFF or animated WebP.
    private static string? DroppedImage(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop)
        && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => RefExt.Contains(Path.GetExtension(f).ToLowerInvariant())
                                        && File.Exists(f) && !MediaKind.IsAnimatedWebp(f))
            : null;

    private void RefDrop_DragOver(object sender, DragEventArgs e)
    {
        // Fires on every mouse move: judge by extension only here; the file
        // is opened (animated-WebP check) once, on drop.
        var plausible = e.Data.GetDataPresent(DataFormats.FileDrop)
                        && e.Data.GetData(DataFormats.FileDrop) is string[] files
                        && files.Any(f => RefExt.Contains(Path.GetExtension(f).ToLowerInvariant()));
        e.Effects = plausible ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void RefDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not StoryboardViewModel vm || !vm.IsAutoPilotIdle) return;
        var path = DroppedImage(e);
        if (path is null) return;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "ref": vm.ReferenceImagePath = path; break;
            case "scene": vm.SceneImagePath = path; break;
            case "outfit": vm.OutfitImagePath = path; break;
        }
    }
}
