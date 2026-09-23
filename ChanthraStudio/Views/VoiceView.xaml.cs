using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class VoiceView : UserControl
{
    public VoiceView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Force our own VM — the ViewSwitcher leaves DataContext inheriting
            // MainViewModel, so a plain `is null` guard never fires.
            if (DataContext is not VoiceViewModel vvm)
                DataContext = new VoiceViewModel(App.Current.Studio);
            else
            {
                vvm.RefreshTakes();
                vvm.RefreshVoiceList();
            }
            // Subscribe to CurrentlyPlaying changes so the MediaElement
            // swaps source and auto-plays whenever the VM signals a new
            // take. (T54 / 7.20)
            if (DataContext is INotifyPropertyChanged inpc)
                inpc.PropertyChanged += OnVmPropertyChanged;
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged inpc)
                inpc.PropertyChanged -= OnVmPropertyChanged;
            try { TakePlayer.Stop(); TakePlayer.Close(); } catch { }
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VoiceViewModel.CurrentlyPlaying))
            LoadCurrentTake();
    }

    private void LoadCurrentTake()
    {
        if (DataContext is not VoiceViewModel vm) return;
        try { TakePlayer.Stop(); } catch { }
        var take = vm.CurrentlyPlaying;
        if (take is null || !System.IO.File.Exists(take.FilePath))
        {
            TakePlayer.Source = null;
            TakePlayGlyph.Text = "▶";
            return;
        }
        try
        {
            TakePlayer.Source = new Uri(take.FilePath, UriKind.Absolute);
            TakePlayer.Play();
            TakePlayGlyph.Text = "⏸";
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("voice", $"failed to play {take.FileName}: {ex.Message}");
        }
    }

    private void TakePlayer_PlayPause(object sender, RoutedEventArgs e)
    {
        if (TakePlayer.Source is null) return;
        try
        {
            if (TakePlayGlyph.Text == "▶")
            {
                TakePlayer.Play();
                TakePlayGlyph.Text = "⏸";
            }
            else
            {
                TakePlayer.Pause();
                TakePlayGlyph.Text = "▶";
            }
        }
        catch { }
    }

    private void TakePlayer_Ended(object sender, RoutedEventArgs e)
    {
        // Loop back to the start instead of unloading — most users want
        // to re-listen without clicking again. The × button at the right
        // edge of the transport still removes the take.
        try
        {
            TakePlayer.Position = TimeSpan.Zero;
            TakePlayer.Play();
        }
        catch { }
    }

    private void TakePlayer_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        ActivityLog.Warn("voice", $"MediaFailed: {e.ErrorException?.Message ?? "unknown"}");
        if (DataContext is VoiceViewModel vm) vm.CurrentlyPlaying = null;
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
