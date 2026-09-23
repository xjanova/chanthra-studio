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

    public GenerateView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GenerateViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is GenerateViewModel vm) vm.PropertyChanged += OnVmPropertyChanged;
            ShowStageVideo();
        };
        Loaded += (_, _) => ShowStageVideo();
        // Close the file when the view goes: a playing preview holds it open
        // and the Library could not delete that clip.
        Unloaded += (_, _) => { StopStageClock(); try { StageVideo.Stop(); StageVideo.Source = null; } catch { } };
    }

    private System.Windows.Threading.DispatcherTimer? _stageClock;
    private bool _stagePaused;

    private void StartStageClock()
    {
        _stageClock ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => UpdateStageBar(), Dispatcher);
        _stageClock.Start();
    }

    private void StopStageClock() => _stageClock?.Stop();

    private void UpdateStageBar()
    {
        if (!StageVideo.NaturalDuration.HasTimeSpan) return;
        var total = StageVideo.NaturalDuration.TimeSpan;
        var pos = StageVideo.Position;
        StageFill.Width = total.TotalMilliseconds <= 0 ? 0
            : Math.Max(0, StageTrack.ActualWidth * Math.Min(1, pos.TotalMilliseconds / total.TotalMilliseconds));
        StageTime.Text = $"{Clock(pos)} / {Clock(total)}";
    }

    private static string Clock(TimeSpan t) =>
        $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private void SetPlayIcon(bool playing) =>
        StagePlayIcon.Data = (System.Windows.Media.Geometry)FindResource(playing ? "IcoPause" : "IcoPlay");

    private void StagePlay_Click(object sender, RoutedEventArgs e)
    {
        if (StageVideo.Source is null) return;
        _stagePaused = !_stagePaused;
        if (_stagePaused) StageVideo.Pause(); else StageVideo.Play();
        SetPlayIcon(!_stagePaused);
    }

    private void StageMute_Click(object sender, RoutedEventArgs e)
    {
        StageVideo.IsMuted = !StageVideo.IsMuted;
        StageMuteIcon.Opacity = StageVideo.IsMuted ? 0.45 : 1.0;
    }

    private void StageOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is GenerateViewModel vm && vm.ActiveShot is { } shot)
            vm.PlayShotCommand.Execute(shot);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GenerateViewModel.StageVideoPath)) ShowStageVideo();
    }

    private void ShowStageVideo()
    {
        var path = (DataContext as GenerateViewModel)?.StageVideoPath;
        try
        {
            StageVideo.Stop();
            StageVideo.Source = path is null ? null : new Uri(path, UriKind.Absolute);
            StageVideoHost.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
            StageBar.Visibility = StageVideoHost.Visibility;
            _stagePaused = false;
            SetPlayIcon(true);
            StageFill.Width = 0;
            StageTime.Text = "00:00 / 00:00";
            if (path is not null && IsLoaded) { StageVideo.Play(); StartStageClock(); }
            else StopStageClock();
        }
        catch { StageVideoHost.Visibility = StageBar.Visibility = Visibility.Collapsed; /* the poster stays up */ }
    }

    private void StageVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        StageVideo.Position = TimeSpan.Zero;
        if (!_stagePaused) StageVideo.Play();
    }

    private void StageVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        // A codec Windows cannot play: fall back to the poster rather than a black box.
        try { StageVideo.Source = null; } catch { }
        StopStageClock();
        StageVideoHost.Visibility = StageBar.Visibility = Visibility.Collapsed;
    }

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
