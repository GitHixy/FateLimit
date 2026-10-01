using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FateLimit.Models;
using FateLimit.Services;

namespace FateLimit.Rendering;

/// <summary>
/// Draws every visible FATE border into the game world each frame, plus the edge marker and the
/// warning banner for the FATE you're closest to leaving.
/// </summary>
public sealed class Overlay
{
    private readonly Plugin plugin;
    private readonly BorderRenderer renderer = new();
    private readonly Dictionary<ushort, float> fade = new();
    private readonly List<FateView> views = [];
    private readonly Projector project;

    /// <summary>The FATE the player was most recently inside, so its border stays while they step back in.</summary>
    private ushort lastInsideId;
    /// <summary>Whether the previous frame was warning, for logging only the moment it starts.</summary>
    private bool wasWarning;

    /// <summary>This frame's evaluated FATEs, for the Diagnostics page.</summary>
    public IReadOnlyList<FateView> Views => views;

    /// <summary>The FATE the warning is about this frame, if any.</summary>
    public FateView? Warning { get; private set; }

    public Overlay(Plugin plugin)
    {
        this.plugin = plugin;
        project = (Vector3 world, out Vector2 screen) => Plugin.GameGui.WorldToScreen(world, out screen, out _);
    }

    public void Draw(double now, float dt, bool visible)
    {
        var config = plugin.Configuration;
        views.Clear();
        Warning = null;

        var player = plugin.Fates.PlayerPosition;
        if (player is null || !config.Enabled)
        {
            fade.Clear();
            return;
        }

        var synced = plugin.Fates.SyncedFateId;
        var style = config.ActiveStyle;
        var reduced = plugin.ReducedMotion;
        var viewport = ImGui.GetMainViewport();
        var pixelScale = Math.Max(viewport.Size.Y / 1080f, 0.5f);
        var dl = ImGui.GetBackgroundDrawList();

        FateView? best = null;
        var stillInside = false;
        var nextInsideId = lastInsideId;

        // Pass 1: where the player stands relative to every FATE.
        foreach (var fate in plugin.Fates.Fates)
        {
            var view = FateLogic.Evaluate(config, fate, player.Value, synced, lastInsideId);
            views.Add(view);

            if (view.Inside && FateLogic.IsDrawable(config, fate))
            {
                // Prefer the FATE you're synced to when FATEs overlap.
                if (!stillInside || fate.Id == synced)
                    nextInsideId = fate.Id;
                stillInside = true;
            }

            if (view.Closeness > 0f && (best is null || view.Closeness > best.Value.Closeness))
                best = view;
        }

        lastInsideId = nextInsideId;

        // Pass 2: fade and draw.
        foreach (var view in views)
        {
            var fate = view.Fate;

            // Fade in and out instead of popping.
            fade.TryGetValue(fate.Id, out var current);
            var rate = Math.Clamp(dt * (view.Visibility > current ? 7f : 4f), 0f, 1f);
            current += (view.Visibility - current) * rate;
            if (Math.Abs(view.Visibility - current) < 0.002f) current = view.Visibility;
            fade[fate.Id] = current;

            if (!visible || current <= 0.003f) continue;

            var segments = TerrainService.SegmentsFor(fate.Radius);
            var frame = new RingFrame
            {
                Center = fate.Center,
                Radius = fate.Radius,
                Heights = config.FollowTerrain ? plugin.Terrain.HeightsFor(fate.Id, fate.Center, fate.Radius, segments, now) : null,
                Segments = segments,
                Style = config.Style,
                Settings = style,
                Alpha = current * config.Opacity,
                WarnBlend = config.WarnColorShift ? view.Closeness : 0f,
                WarnColor = config.WarnColor,
                Viewer = player.Value,
                FocusRadius = config.FocusRadius,
                Time = (float)now,
                ReducedMotion = reduced,
                PixelScale = pixelScale,
                Project = project,
            };

            renderer.Draw(dl, frame);

            if (config.ShowEdgeMarker && best is { } b && b.Fate.Id == fate.Id)
                renderer.DrawEdgeMarker(dl, frame, MarkerColor(config), view.Closeness * config.Opacity);
        }

        // Forget fades of FATEs that ended.
        if (fade.Count > views.Count)
        {
            var alive = new HashSet<ushort>();
            foreach (var v in views) alive.Add(v.Fate.Id);
            foreach (var id in new List<ushort>(fade.Keys))
                if (!alive.Contains(id)) fade.Remove(id);
        }

        Warning = best;

        if (best is { } warning)
        {
            if (!wasWarning && config.LogEvents)
                Plugin.Log.Debug("Edge warning for FATE {Id}: {Edge:F1}y from the edge", warning.Fate.Id, warning.EdgeDistance);
            wasWarning = true;

            if (visible && config.ShowBanner)
                DrawBanner(warning, viewport.Pos + new Vector2(viewport.Size.X * 0.5f, viewport.Size.Y * config.BannerHeight), pixelScale, (float)now, reduced);
        }
        else
        {
            wasWarning = false;
        }
    }

    public static Vector4 MarkerColor(Configuration config)
        => config.WarnColorShift ? config.WarnColor : config.ActiveStyle.Color;

    private void DrawBanner(FateView view, Vector2 at, float pixelScale, float time, bool reduced)
    {
        var config = plugin.Configuration;
        var (title, detail) = BannerText(view);
        Banner.Draw(ImGui.GetBackgroundDrawList(), at, config.BannerScale * pixelScale, title, detail,
            config.WarnColor, Math.Clamp(view.Closeness * 1.6f, 0f, 1f) * config.Opacity, time, reduced);
    }

    public static (string Title, string Detail) BannerText(FateView view)
        => view.Inside
            ? ("EDGE OF THE FATE", $"{view.EdgeDistance:0.0} yalms to the border")
            : ("OUTSIDE THE FATE", $"{-view.EdgeDistance:0.0} yalms back to the border");

    /// <summary>Called on zone changes: nothing from the old zone should linger.</summary>
    public void Reset()
    {
        fade.Clear();
        lastInsideId = 0;
        wasWarning = false;
    }
}
