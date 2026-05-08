using System.Windows;
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

    public string OutputName => NameBox.Text;
    public double SecondsPerClip => DurationSlider.Value;
    public int Fps => Fps24.IsChecked == true ? 24 : Fps60.IsChecked == true ? 60 : 30;

    /// <summary>Empty when the user hasn't picked an audio file.</summary>
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
        AudioPath = dlg.FileName;
        AudioPathLabel.Text = System.IO.Path.GetFileName(AudioPath);
        AudioPathLabel.Foreground = (Brush)FindResource("BrushText1");
    }

    private void ClearAudio_Click(object sender, RoutedEventArgs e)
    {
        AudioPath = "";
        AudioPathLabel.Text = "(silent — no audio track)";
        AudioPathLabel.Foreground = (Brush)FindResource("BrushText3");
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
