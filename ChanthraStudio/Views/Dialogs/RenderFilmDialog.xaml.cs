using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChanthraStudio.Services;
using Microsoft.Win32;

namespace ChanthraStudio.Views.Dialogs;

public partial class RenderFilmDialog : Window
{
    public string Subtitle { get; }
    public string FfmpegStatusLabel { get; }
    public Brush FfmpegStatusBrush { get; }
    public bool FfmpegFound { get; }

    /// <summary>Recent voice takes from media/voice/, newest first.</summary>
    public List<VoiceTake> RecentTakes { get; } = new();
    public bool HasRecentTakes => RecentTakes.Count > 0;

    public string OutputName => NameBox.Text;
    public double SecondsPerClip => DurationSlider.Value;
    public int Fps => Fps24.IsChecked == true ? 24 : Fps60.IsChecked == true ? 60 : 30;

    public string AudioPath { get; private set; } = "";
    public double AudioVolume => VolumeSlider.Value;

    public bool Confirmed { get; private set; }

    public RenderFilmDialog(StudioContext ctx, int clipCount, string defaultName)
    {
        Subtitle = $"Combining {clipCount} clip{(clipCount == 1 ? "" : "s")} into a single MP4 via ffmpeg.";

        var ffmpegPath = ctx.FFmpeg.TryResolve();
        FfmpegFound = ffmpegPath is not null;
        FfmpegStatusLabel = ffmpegPath is null
            ? "ffmpeg not found — install via `winget install Gyan.FFmpeg` or set path in Settings"
            : $"ffmpeg ready · {ffmpegPath}";
        FfmpegStatusBrush = new SolidColorBrush(ffmpegPath is null
            ? Color.FromArgb(0xFF, 0xD0, 0x5A, 0x5A)
            : Color.FromArgb(0xFF, 0x6D, 0xBF, 0x8C));

        // Pull voice takes so the user can skip the Browse… → file picker step
        // when they just generated a take in the Voice atelier.
        foreach (var t in ctx.VoiceService.ListTakes(20))
            RecentTakes.Add(t);

        InitializeComponent();
        DataContext = this;
        NameBox.Text = defaultName;
    }

    private void BrowseAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick an audio track",
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        SetAudio(dlg.FileName);
        // Picking a file manually clears any take-dropdown selection.
        RecentTakesBox.SelectedIndex = -1;
    }

    private void ClearAudio_Click(object sender, RoutedEventArgs e)
    {
        SetAudio("");
        RecentTakesBox.SelectedIndex = -1;
    }

    private void RecentTakes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentTakesBox.SelectedItem is VoiceTake t)
            SetAudio(t.FilePath);
    }

    private void SetAudio(string path)
    {
        AudioPath = path;
        if (string.IsNullOrEmpty(path))
        {
            AudioPathLabel.Text = "(silent — no audio track)";
            AudioPathLabel.Foreground = (Brush)FindResource("BrushText3");
        }
        else
        {
            AudioPathLabel.Text = System.IO.Path.GetFileName(path);
            AudioPathLabel.Foreground = (Brush)FindResource("BrushText1");
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
