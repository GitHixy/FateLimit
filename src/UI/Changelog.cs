using System;
using System.Collections.Generic;
using System.Linq;

namespace FateLimit.UI;

/// <summary>What a single release note is about, which drives its colour and tag.</summary>
public enum NoteKind
{
    Added,
    Changed,
    Improved,
    Fixed,
    Removed,
}

/// <summary>One line in a release's notes.</summary>
public readonly record struct Note(NoteKind Kind, string Text);

/// <summary>One published version and everything that went into it.</summary>
public readonly record struct Release(string Version, string Headline, Note[] Notes);

/// <summary>
/// The release notes shown after an update. Newest first - add a new entry at the top
/// whenever the assembly version in FateLimit.csproj changes.
/// </summary>
public static class Changelog
{
    public static readonly Release[] Releases =
    [
        new("0.1.0", "Welcome to FateLimit",
        [
            new(NoteKind.Added, "FateLimit draws the border of FATE areas right in the world, so you can see where the fight "
                                + "ends and stay inside it."),
            new(NoteKind.Added, "Five border styles: Aether ring, Barrier wall, Marching dashes, Crystal runes and FATE ember. "
                                + "Each has its own colour, thickness, height, spacing, glow and animation (Settings → Style)."),
            new(NoteKind.Added, "Show borders all the time, or only when you get close to the edge of the FATE you're in "
                                + "(Settings → Visibility)."),
            new(NoteKind.Added, "As you near the edge the border turns to a warning colour, a banner shows the distance left and "
                                + "a marker hovers over the closest point of the border."),
            new(NoteKind.Added, "Borders follow the ground over hills and slopes."),
            new(NoteKind.Added, "/fatelimit test places a practice FATE around you to try every style."),
            new(NoteKind.Added, "A Diagnostics page with nearby FATEs, a live log and Copy report, plus buttons to report a bug "
                                + "or suggest a feature on GitHub."),
        ]),
    ];

    /// <summary>The running assembly's version, formatted the way the notes list them.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>The newest release, or null when the list is empty.</summary>
    public static Release? Latest => Releases.Length > 0 ? Releases[0] : null;

    /// <summary>
    /// Everything published since <paramref name="sinceVersion"/>. An unknown or missing
    /// version yields just the newest release, so an upgrade never dumps the whole history.
    /// </summary>
    public static IReadOnlyList<Release> Since(string sinceVersion)
    {
        if (!Version.TryParse(sinceVersion, out var seen))
            return Latest.HasValue ? [Latest.Value] : [];

        return Releases
            .Where(r => Version.TryParse(r.Version, out var v) && v > seen)
            .ToList();
    }

    public static string KindLabel(NoteKind kind) => kind switch
    {
        NoteKind.Added => "NEW",
        NoteKind.Changed => "CHANGED",
        NoteKind.Improved => "IMPROVED",
        NoteKind.Fixed => "FIXED",
        NoteKind.Removed => "REMOVED",
        _ => "NOTE",
    };

    public static System.Numerics.Vector4 KindColor(NoteKind kind) => kind switch
    {
        NoteKind.Added => Theme.Good,
        NoteKind.Changed => Theme.Crystal,
        NoteKind.Improved => Theme.Gold,
        NoteKind.Fixed => Theme.Warn,
        NoteKind.Removed => Theme.Bad,
        _ => Theme.TextMuted,
    };
}
