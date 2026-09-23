using System;
using System.Collections.Generic;
using System.IO;

namespace ChanthraStudio.Services;

/// <summary>
/// One answer to "is this file a picture, a moving picture or a sound".
///
/// Seven places used to decide this by their own extension lists, and they
/// disagreed: a .gif was a video to the renderer and a still to the Library, a
/// .ts was in neither Library chip, and an animated .webp — what ComfyUI's
/// SaveAnimatedWEBP writes — was a still everywhere, so the renderer looped it
/// as an image ffmpeg cannot decode and never finished.
/// </summary>
public static class MediaKind
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".mpg", ".mpeg", ".wmv", ".flv", ".ts", ".m2ts",
    };

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma",
    };

    /// <summary>A container of moving pictures, by extension.</summary>
    public static bool IsVideo(string? path) => VideoExt.Contains(Ext(path));

    /// <summary>A picture file, by extension (animated or not).</summary>
    public static bool IsImage(string? path) => ImageExt.Contains(Ext(path));

    public static bool IsAudio(string? path) => AudioExt.Contains(Ext(path));

    /// <summary>
    /// Frames ffmpeg reads as a stream rather than a still: real video, and
    /// GIFs, which its GIF decoder plays in full.
    /// </summary>
    public static bool RendersAsVideo(string? path)
        => IsVideo(path) || string.Equals(Ext(path), ".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for an animated WebP. ffmpeg's WebP decoder skips the ANIM/ANMF
    /// chunks and finds no image data, so such a file can be neither rendered
    /// nor played; callers refuse it with a message instead of hanging.
    /// </summary>
    /// <remarks>Reads the VP8X header only: bytes 0–3 "RIFF", 8–11 "WEBP",
    /// 12–15 "VP8X", and bit 1 of byte 20 is the animation flag.</remarks>
    public static bool IsAnimatedWebp(string? path)
    {
        if (!string.Equals(Ext(path), ".webp", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        try
        {
            Span<byte> head = stackalloc byte[21];
            using var fs = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(head) < head.Length) return false;
            return head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P'
                && head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'X'
                && (head[20] & 0x02) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Ext(string? path)
        => string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path);
}
