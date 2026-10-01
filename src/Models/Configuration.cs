using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json;

namespace FateLimit.Models;

public enum ReducedMotionMode
{
    /// <summary>Follow Dalamud's reduced motion setting.</summary>
    Auto,
    On,
    Off,
}

/// <summary>When borders are drawn at all.</summary>
public enum VisibilityMode
{
    /// <summary>Every running FATE nearby is outlined all the time.</summary>
    Always,
    /// <summary>Only the FATE you're in, and only once you get close to its edge.</summary>
    NearEdge,
}

/// <summary>Configuration settings for the FateLimit plugin.</summary>
[Serializable]
public class Configuration
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch for everything drawn in the world.</summary>
    public bool Enabled { get; set; } = true;

    // Look
    public BorderStyle Style { get; set; } = BorderStyle.Aether;
    /// <summary>Per-style settings, so switching style never loses a tuned colour.</summary>
    public Dictionary<BorderStyle, StyleSettings> Styles { get; set; } = new();
    /// <summary>Multiplies the opacity of every border.</summary>
    public float Opacity { get; set; } = 0.9f;
    /// <summary>Lays the border over the terrain instead of on the FATE's flat plane.</summary>
    public bool FollowTerrain { get; set; } = true;
    public ReducedMotionMode ReducedMotion { get; set; } = ReducedMotionMode.Auto;

    // When to show
    public VisibilityMode Visibility { get; set; } = VisibilityMode.NearEdge;
    /// <summary>Distance from the edge, in yalms, where the warning begins.</summary>
    public float WarnDistance { get; set; } = 8f;
    /// <summary>Also show a FATE's edge as you walk up to it from outside.</summary>
    public bool WarnFromOutside { get; set; }
    /// <summary>In Always mode, FATEs whose edge is farther than this are skipped.</summary>
    public float MaxDrawDistance { get; set; } = 150f;
    /// <summary>In Always mode, outline only the FATE you're standing in.</summary>
    public bool OnlyCurrentFate { get; set; }
    /// <summary>Only warn about the edge while you're level-synced to that FATE.</summary>
    public bool OnlyWhenSynced { get; set; }
    /// <summary>Also outline FATEs still waiting for someone to start them.</summary>
    public bool ShowPreparing { get; set; }
    /// <summary>Parts of the ring farther than this from you fade out. 0 draws the whole ring.</summary>
    public float FocusRadius { get; set; } = 30f;

    // Warning
    /// <summary>Blend the border toward the warning colour as you near the edge.</summary>
    public bool WarnColorShift { get; set; } = true;
    public Vector4 WarnColor { get; set; } = new(0.93f, 0.33f, 0.28f, 1f);
    /// <summary>Banner at the top of the screen with the distance left to the edge.</summary>
    public bool ShowBanner { get; set; } = true;
    /// <summary>Banner position as a fraction of screen height.</summary>
    public float BannerHeight { get; set; } = 0.18f;
    public float BannerScale { get; set; } = 1f;
    /// <summary>A marker on the point of the border closest to you.</summary>
    public bool ShowEdgeMarker { get; set; } = true;

    // Diagnostics
    /// <summary>Write FATE appearances and edge warnings to the Diagnostics log.</summary>
    public bool LogEvents { get; set; }

    // Release notes
    /// <summary>Last plugin version whose changelog the user dismissed.</summary>
    public string LastSeenVersion { get; set; } = string.Empty;

    /// <summary>Settings of the active style, created from its defaults on first use.</summary>
    [JsonIgnore]
    public StyleSettings ActiveStyle => StyleFor(Style);

    public StyleSettings StyleFor(BorderStyle style)
    {
        if (!Styles.TryGetValue(style, out var settings) || settings is null)
        {
            settings = StyleSettings.DefaultsFor(style);
            Styles[style] = settings;
        }
        return settings;
    }
}
