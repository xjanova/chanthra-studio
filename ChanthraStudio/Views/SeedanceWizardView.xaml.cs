using System;
using System.IO;
using System.Linq;
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
        {
            mat.Label = Path.GetFileName(dlg.FileName);
            mat.FilePath = dlg.FileName;
        }
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

        // The frame, the length and the attached image travel with the
        // prompt; the Composer used to render a 9:16 lip-sync plan at its own
        // 16:9 / 8 s with no reference image at all.
        var gen = main.Generate;
        gen.Prompt = vm.AssembledPrompt;
        gen.Aspect = vm.ActiveAspectId switch
        {
            "9:16" => Models.AspectRatio.Vertical,
            "1:1" => Models.AspectRatio.Square,
            "21:9" => Models.AspectRatio.Cinema,
            _ => Models.AspectRatio.Wide,
        };
        gen.DurationSec = vm.DurationSec;
        // Always the wizard's image — or none. Keeping the Composer's previous
        // reference uploaded an unrelated picture as this plan's first frame.
        gen.ReferenceImagePath = vm.PrimaryImagePath;
        // The size the plan was written for: Seedance bills 1080p when HD is
        // on, and the Composer's default had it on for a 480p/720p plan.
        gen.Hd4k = vm.ActiveResolutionId == "1080p";
        // The Composer wraps every prompt in its own style, camera and motion
        // clauses; make them agree with the plan instead of fighting it — no
        // house style in front of the @references, the plan's own move, and
        // a still head for lip-sync.
        gen.ActiveStyleId = "";
        gen.Camera = vm.IsLipSync && vm.LockCamera ? Models.CamMode.Locked : vm.ActiveMoveId switch
        {
            "locked" => Models.CamMode.Locked,
            "panL" or "panR" => Models.CamMode.Pan,
            "tilt" => Models.CamMode.Tilt,
            "orbit" => Models.CamMode.Orbit,
            "pull" or "dolly" or "follow" or "onetake" => Models.CamMode.Dolly,
            _ => Models.CamMode.Push,
        };
        if (vm.IsLipSync) gen.Motion = Math.Min(gen.Motion, 0.2);
        // The @Image1-style references only mean something to Seedance.
        var seedance = gen.VideoRoutes.FirstOrDefault(r => r.Id == "seedance");
        if (seedance is not null) gen.ActiveRoute = seedance;
        main.ActiveView = AppView.Generate;
    }
}
