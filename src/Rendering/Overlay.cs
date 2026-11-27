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
public sealed unsafe class Overlay
{
    private readonly Plugin plugin;
    private readonly BorderRenderer renderer = new();
    private readonly Dictionary<ushort, float> fade = new();
    private readonly List<FateView> views = [];
    private readonly Projector project;
    private readonly UiMaskService uiMask = new();
    private readonly BodyMask body = new();
    private readonly List<(RingFrame Frame, float Marker)> frames = [];
    private readonly List<Vector4> holes = [];
    private readonly List<Vector4> freeA = [];
    private readonly List<Vector4> freeB = [];
    private readonly List<Vector4> freeUi = [];
    private ushort[] idxScratch = new ushort[8192];

    /// <summary>The FATE the player was most recently inside, so its border stays while they step back in.</summary>
    private ushort lastInsideId;
    /// <summary>Whether the previous frame was warning, for logging only the moment it starts.</summary>
    private bool wasWarning;

    /// <summary>This frame's evaluated FATEs, for the Diagnostics page.</summary>
    public IReadOnlyList<FateView> Views => views;

    /// <summary>The FATE the warning is about this frame, if any.</summary>
    public FateView? Warning { get; private set; }

    /// <summary>Game windows the borders were kept out of this frame, for the Diagnostics page.</summary>
    public int MaskedWindows { get; private set; }

    /// <summary>Diagnostics only: outlines the game windows borders are kept out of.</summary>
    public bool ShowUiMask { get; set; }

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

        if (config.HideBehindPlayer && visible) body.Update(project);
        else body.Clear();

        // Pass 2: fade and build each ring's frame.
        frames.Clear();
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
            var heights = config.FollowTerrain ? plugin.Terrain.HeightsFor(fate.Id, fate.Center, fate.Radius, segments, now) : null;
            var frame = new RingFrame
            {
                Center = fate.Center,
                Radius = fate.Radius,
                Heights = heights,
                Occlusion = config.LineOfSight
                    ? plugin.Occlusion.VisibilityFor(fate.Id, fate.Center, fate.Radius, segments, heights, SightLift(config), now, dt)
                    : null,
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
            var marker = config.ShowEdgeMarker && best is { } b && b.Fate.Id == fate.Id ? view.Closeness * config.Opacity : 0f;
            frames.Add((frame, marker));
        }

        // Pass 3: draw. The game's windows and the player's box are cut out of the screen; the rings
        // are drawn once into what's left, then again inside the player's box with whatever runs
        // behind the player turned off.
        MaskedWindows = 0;
        holes.Clear();
        if (visible && config.HideBehindUi)
        {
            uiMask.Refresh(viewport.Pos, viewport.Size);
            holes.AddRange(uiMask.Windows);
            MaskedWindows = holes.Count;
        }
        if (visible && ShowUiMask) DrawMaskDebug(config.HideBehindUi);

        var screen = new Vector4(viewport.Pos.X, viewport.Pos.Y, viewport.Pos.X + viewport.Size.X, viewport.Pos.Y + viewport.Size.Y);
        if (frames.Count > 0)
        {
            if (body.Active)
            {
                // The player's silhouette, minus any window over it.
                uiMask.Partition(screen, holes, freeUi);
                Intersect(freeUi, body.Strips, freeB);
                holes.AddRange(body.Strips);
            }

            uiMask.Partition(screen, holes, freeA);
            DrawFrames(dl, freeA, false, config);
            if (body.Active) DrawFrames(dl, freeB, true, config);
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

    private void DrawFrames(ImDrawListPtr dl, List<Vector4> free, bool behindPlayer, Configuration config)
    {
        if (free.Count == 0) return;
        var whole = free.Count == 1 && !behindPlayer && holes.Count == 0;
        var mask = whole ? default : BeginMask(dl, free[0]);

        foreach (var (f, marker) in frames)
        {
            var frame = f;
            if (behindPlayer)
                frame.Occlusion = body.Behind(frame.Occlusion, frame.Center, frame.Radius, frame.Segments, frame.Heights, SightLift(config));
            renderer.Draw(dl, frame);
            if (marker > 0f)
                renderer.DrawEdgeMarker(dl, frame, MarkerColor(config), marker);
        }

        if (!whole) EndMask(dl, mask, free);
    }

    /// <summary>Every non-empty overlap between a rectangle of <paramref name="a"/> and one of <paramref name="b"/>.</summary>
    private static void Intersect(List<Vector4> a, IReadOnlyList<Vector4> b, List<Vector4> output)
    {
        output.Clear();
        foreach (var r in a)
        foreach (var clip in b)
        {
            var c = new Vector4(Math.Max(r.X, clip.X), Math.Max(r.Y, clip.Y), Math.Min(r.Z, clip.Z), Math.Min(r.W, clip.W));
            if (c.X < c.Z && c.Y < c.W) output.Add(c);
        }
    }

    private void DrawMaskDebug(bool windows)
    {
        var fg = ImGui.GetForegroundDrawList();
        for (var i = 0; windows && i < uiMask.Windows.Count; i++)
        {
            var w = uiMask.Windows[i];
            fg.AddRect(new Vector2(w.X, w.Y), new Vector2(w.Z, w.W), 0xFF00C8FF, 0f, ImDrawFlags.None, 1.5f);
            fg.AddText(new Vector2(w.X + 3, w.Y + 2), 0xFF00C8FF, uiMask.Names[i]);
        }
        if (body.Active)
        {
            var color = body.FromSkeleton ? 0xC0FF80FFu : 0xC00080FFu;
            foreach (var r in body.Strips)
                fg.AddRect(new Vector2(r.X, r.Y), new Vector2(r.Z, r.W), color, 0f, ImDrawFlags.None, 1f);
        }
    }

    private readonly record struct MaskStart(int Cmd, int Idx);

    private static MaskStart BeginMask(ImDrawListPtr dl, Vector4 first)
    {
        dl.PushClipRect(new Vector2(first.X, first.Y), new Vector2(first.Z, first.W), false);
        return new MaskStart(Math.Max(dl.CmdBuffer.Size - 1, 0), dl.IdxBuffer.Size);
    }

    /// <summary>
    /// Replays the indices drawn since <see cref="BeginMask"/> under the clip of every other free
    /// rectangle, reusing the same vertices so the border is only built once per frame.
    /// </summary>
    private void EndMask(ImDrawListPtr dl, MaskStart start, List<Vector4> free)
    {
        dl.PopClipRect();
        if (free.Count < 2) return;

        var cmds = dl.CmdBuffer;
        if (cmds.Size == 0) return;
        var last = cmds.Data[cmds.Size - 1];

        // Only commands that share the current vertex offset and texture can be replayed with the
        // public API; anything else (a vertex-buffer wrap mid-frame) stays in the first rectangle.
        var total = 0;
        for (var c = start.Cmd; c < cmds.Size; c++)
        {
            var cmd = cmds.Data[c];
            if (cmd.ElemCount == 0 || cmd.IdxOffset < start.Idx) continue;
            if (cmd.VtxOffset != last.VtxOffset || cmd.TextureId != last.TextureId) continue;
            if (total + cmd.ElemCount > idxScratch.Length)
                Array.Resize(ref idxScratch, Math.Max(idxScratch.Length * 2, total + (int)cmd.ElemCount));
            var src = dl.IdxBuffer.Data + cmd.IdxOffset;
            for (var k = 0; k < cmd.ElemCount; k++) idxScratch[total + k] = src[k];
            total += (int)cmd.ElemCount;
        }
        if (total == 0) return;

        // Screen bounds of what was drawn, so rectangles it never reaches are skipped.
        var vtx = dl.VtxBuffer.Data + last.VtxOffset;
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        for (var k = 0; k < total; k++)
        {
            var v = vtx[idxScratch[k]].Pos;
            lo = Vector2.Min(lo, v);
            hi = Vector2.Max(hi, v);
        }

        for (var r = 1; r < free.Count; r++)
        {
            var rect = free[r];
            if (rect.X > hi.X || rect.Z < lo.X || rect.Y > hi.Y || rect.W < lo.Y) continue;
            dl.PushClipRect(new Vector2(rect.X, rect.Y), new Vector2(rect.Z, rect.W), false);
            dl.PrimReserve(total, 0);
            // PrimReserve grows the index buffer by exactly `total`; fill that tail directly.
            var idx = dl.IdxBuffer;
            idxScratch.AsSpan(0, total).CopyTo(new Span<ushort>(idx.Data + idx.Size - total, total));
            dl.PopClipRect();
        }
    }

    /// <summary>
    /// How high above the ground line-of-sight rays aim: just over the line itself, or partway up
    /// styles that stand off the ground, so a wall whose top shows over a ridge still counts as seen.
    /// </summary>
    private static float SightLift(Configuration config) => config.Style switch
    {
        BorderStyle.Barrier => 0.35f + Math.Max(config.ActiveStyle.Height, 0.3f) * 0.5f,
        BorderStyle.Runes => 0.35f + Math.Max(config.ActiveStyle.Height, 0.4f) * 0.5f,
        _ => 0.35f,
    };

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
