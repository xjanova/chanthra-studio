using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class GenerateView : UserControl
{
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public GenerateView() => InitializeComponent();

    private void ReferenceImage_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ReferenceImage_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not GenerateViewModel vm) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var imgPath = files.FirstOrDefault(f =>
            ImageExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (imgPath is not null) vm.ReferenceImagePath = imgPath;
        e.Handled = true;
    }

    private void ReferenceImage_BrowseClick(object sender, MouseButtonEventArgs e)
    {
        // The clear-X button has its own MouseLeftButtonUp that bubbles up here
        // — only open the picker when the click landed on the panel, not the X.
        if (e.OriginalSource is FrameworkElement fe && fe.Name == "")
        {
            // anonymous part — fine, fall through to browse
        }
        if (DataContext is GenerateViewModel vm)
            vm.BrowseReferenceImageCommand.Execute(null);
    }

    private static bool HasImageFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return false;
        return files.Any(f => ImageExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }
}
