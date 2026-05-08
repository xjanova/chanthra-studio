namespace ChanthraStudio.Models;

public enum ShotStatus { Queue, Generating, Done, Error }

public enum CamMode { Locked, Pan, Tilt, Push, Orbit, Dolly }

public enum AspectRatio { Vertical, Square, Wide, Cinema }

public sealed class Shot
{
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";          // "03"
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string StyleId { get; set; } = "empress";
    public string ModelId { get; set; } = "chanthra-sora-lyra-2.4";
    public AspectRatio Aspect { get; set; } = AspectRatio.Wide;
    public double DurationSec { get; set; } = 8.0;
    public double Motion { get; set; } = 0.7;
    public (int A, int B) Seed { get; set; } = (2814, 9217);
    public bool Hd4k { get; set; } = true;
    public bool Audio { get; set; } = true;
    public CamMode Cam { get; set; } = CamMode.Locked;
    public List<string> Tags { get; } = new();
    public ShotStatus Status { get; set; } = ShotStatus.Queue;
    public double Progress { get; set; }              // 0..100
    public string? ThumbUrl { get; set; }
    public string? VideoUrl { get; set; }
    public string DurationLabel { get; set; } = "8.0s";
    public string Description { get; set; } = "";
}

public sealed class Sequence
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> ShotIds { get; } = new();
}

public sealed class Project
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> SequenceIds { get; } = new();
    public string CurrentShotId { get; set; } = "";
}

public sealed class StylePreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ThumbPath { get; set; } = "";
}
