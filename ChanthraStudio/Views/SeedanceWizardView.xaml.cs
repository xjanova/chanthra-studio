using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class SeedanceWizardView : UserControl
{
    public SeedanceWizardView()
    {
        InitializeComponent();

        // The view is hosted in MainWindow's ViewSwitcher with no explicit
        // DataContext, so it INHERITS MainViewModel from the ContentControl
        // (confirmed: a `??=`/`is null` guard never fires because the inherited
        // value isn't null). Set our own VM unconditionally so every binding
        // resolves against the wizard, not the shell.
        DataContext = new SeedanceWizardViewModel();
    }

    /// <summary>
    /// Optional per-material file attach — the wizard authors a prompt, so a
    /// real file isn't required, but picking one fills in the label/filename
    /// for clarity. Filter follows the material's kind.
    /// </summary>
    private void AttachMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SeedMaterial mat) return;

        var filter = mat.Kind switch
        {
            MaterialKind.Video => "Video|*.mp4;*.mov;*.webm;*.mkv;*.m4v;*.avi|All files|*.*",
            MaterialKind.Audio => "Audio|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg|All files|*.*",
            _ => "Image|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*",
        };

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Attach a file for {mat.RefName}",
            Filter = filter,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            mat.Label = Path.GetFileName(dlg.FileName);
    }

    /// <summary>
    /// Hand the assembled prompt to the Generate composer and switch to it —
    /// a real cross-view action, reached through the window's MainViewModel
    /// rather than coupling the wizard VM to the app shell.
    /// </summary>
    private void SendToComposer_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SeedanceWizardViewModel vm) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        if (string.IsNullOrWhiteSpace(vm.AssembledPrompt))
        {
            vm.StatusMessage = "Nothing to send yet — describe the shot first.";
            return;
        }

        main.Generate.Prompt = vm.AssembledPrompt;
        main.ActiveView = AppView.Generate;
    }
}
