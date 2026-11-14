namespace HitboxViewer;

/// <summary>ModConfig\HitboxViewer.json. Created with these defaults on first run.</summary>
public sealed class HitboxViewerConfig
{
    /// <summary>Mode at game start: Off, Target or Radius.</summary>
    public string DefaultMode { get; set; } = "Off";

    /// <summary>Labels on by default in TARGET mode.</summary>
    public bool LabelsTarget { get; set; } = true;

    /// <summary>Labels on by default in RADIUS mode.</summary>
    public bool LabelsRadius { get; set; } = false;
    /// <summary>Labels (zone + multiplier) in KEYPOINTS mode.</summary>
    public bool LabelsKeypoints { get; set; } = true;

    /// <summary>RADIUS mode search radius in blocks.</summary>
    public double Radius { get; set; } = 24;

    /// <summary>RADIUS mode entity cap (nearest N).</summary>
    public int EntityCap { get; set; } = 20;

    /// <summary>Nothing is drawn beyond this distance in any mode.</summary>
    public double MaxDrawDist { get; set; } = 32;

    /// <summary>Wireframe line width.</summary>
    public float LineWidth { get; set; } = 1.6f;

    /// <summary>Folder for .hbv dump JSON. Falls back to VintagestoryData\hitboxviewer when it cannot be created.</summary>
    public string DumpDir { get; set; } = @"C:\Work\vs\vs_scratch\hitbox-viewer-20261112\out";
}
