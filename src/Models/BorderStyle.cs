using System;
using System.Numerics;
using Dalamud.Interface;

namespace FateLimit.Models;

/// <summary>The look of a FATE's border in the world.</summary>
public enum BorderStyle
{
    /// <summary>A softly glowing ring of aether with a shimmer running along it.</summary>
    Aether,
    /// <summary>A translucent wall rising from the ground, like the barriers around an arena.</summary>
    Barrier,
    /// <summary>Dashes marching around the ring, like the outline of a ground marker.</summary>
    Dashes,
    /// <summary>Floating crystal diamonds on light pillars, linked by a thin thread.</summary>
    Runes,
    /// <summary>A warm band fading inward from the edge, like the FATE circle on the map.</summary>
    Ember,
}

/// <summary>Every tunable of one border style. Not every style reads every field.</summary>
[Serializable]
public class StyleSettings
{
    public Vector4 Color { get; set; } = new(0.44f, 0.72f, 0.88f, 1f);
    /// <summary>Line thickness in pixels at a middling distance.</summary>
    public float Thickness { get; set; } = 3f;
    /// <summary>Wall height, pillar height or band width, in yalms.</summary>
    public float Height { get; set; } = 2f;
    /// <summary>Dash length or distance between runes, in yalms.</summary>
    public float Spacing { get; set; } = 2f;
    /// <summary>Strength of the soft halo around lines, 0 to 1.</summary>
    public float Glow { get; set; } = 0.6f;
    public bool Animate { get; set; } = true;
    public float Speed { get; set; } = 1f;

    public StyleSettings Clone() => (StyleSettings)MemberwiseClone();

    public static StyleSettings DefaultsFor(BorderStyle style) => style switch
    {
        BorderStyle.Barrier => new StyleSettings
        {
            Color = new(0.44f, 0.72f, 0.88f, 1f), Thickness = 2f, Height = 2.5f, Spacing = 3f, Glow = 0.5f,
        },
        BorderStyle.Dashes => new StyleSettings
        {
            Color = new(0.95f, 0.84f, 0.59f, 1f), Thickness = 3f, Height = 0f, Spacing = 1.6f, Glow = 0.35f, Speed = 0.6f,
        },
        BorderStyle.Runes => new StyleSettings
        {
            Color = new(0.62f, 0.85f, 0.96f, 1f), Thickness = 1.5f, Height = 1.6f, Spacing = 5f, Glow = 0.6f,
        },
        BorderStyle.Ember => new StyleSettings
        {
            Color = new(1f, 0.62f, 0.25f, 1f), Thickness = 2.5f, Height = 3f, Spacing = 0f, Glow = 0.45f, Speed = 0.7f,
        },
        _ => new StyleSettings
        {
            Color = new(0.44f, 0.72f, 0.88f, 1f), Thickness = 3f, Height = 0f, Spacing = 0f, Glow = 0.6f,
        },
    };
}

public static class BorderStyleInfo
{
    public static readonly BorderStyle[] All = Enum.GetValues<BorderStyle>();

    public static string Name(BorderStyle style) => style switch
    {
        BorderStyle.Aether => "Aether ring",
        BorderStyle.Barrier => "Barrier wall",
        BorderStyle.Dashes => "Marching dashes",
        BorderStyle.Runes => "Crystal runes",
        BorderStyle.Ember => "FATE ember",
        _ => style.ToString(),
    };

    public static string Blurb(BorderStyle style) => style switch
    {
        BorderStyle.Aether => "A softly glowing ring with a shimmer running along it.",
        BorderStyle.Barrier => "A translucent wall rising from the ground, like an arena barrier.",
        BorderStyle.Dashes => "Dashes marching around the edge, like a ground marker's outline.",
        BorderStyle.Runes => "Floating crystal diamonds on pillars of light, linked by a thread.",
        BorderStyle.Ember => "A warm band fading inward from the edge, like the map's FATE circle.",
        _ => string.Empty,
    };

    public static FontAwesomeIcon Icon(BorderStyle style) => style switch
    {
        BorderStyle.Aether => FontAwesomeIcon.CircleNotch,
        BorderStyle.Barrier => FontAwesomeIcon.ShieldAlt,
        BorderStyle.Dashes => FontAwesomeIcon.EllipsisH,
        BorderStyle.Runes => FontAwesomeIcon.Gem,
        BorderStyle.Ember => FontAwesomeIcon.Fire,
        _ => FontAwesomeIcon.Circle,
    };

    /// <summary>Which sliders mean something for a style, and what they're called there.</summary>
    public static string? HeightLabel(BorderStyle style) => style switch
    {
        BorderStyle.Barrier => "Wall height",
        BorderStyle.Runes => "Pillar height",
        BorderStyle.Ember => "Band width",
        _ => null,
    };

    public static string? SpacingLabel(BorderStyle style) => style switch
    {
        BorderStyle.Barrier => "Streak spacing",
        BorderStyle.Dashes => "Dash length",
        BorderStyle.Runes => "Rune spacing",
        _ => null,
    };
}
