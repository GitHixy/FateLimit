using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FateLimit.Services;

/// <summary>
/// Works out where the game's own windows are on screen, so borders can be kept out of them.
/// Dalamud draws after the game UI, so without this a border would paint over the hotbars, chat
/// and party list. The screen minus every visible window is split into rectangles that the
/// renderer then clips to.
/// </summary>
public sealed unsafe class UiMaskService
{
    /// <summary>Addons that are drawn over the whole screen or only hold floating text, never a window.</summary>
    private static readonly HashSet<string> Ignored =
    [
        "NamePlate", "_FlyText", "_ScreenText", "_PopUpText", "_MiniTalk", "_TextError", "_TextClassChange",
        "_WideText", "_AreaText", "_LocationTitle", "_LocationTitleShort", "FadeMiddle", "FadeBack", "FadeBlack",
        "ScreenFrameSystem", "_Image", "_Image3", "_TargetCursor", "_TargetCursorGround", "_LimitBreak",
    ];

    private readonly List<Vector4> windows = [];
    private readonly List<string> names = [];
    private readonly List<float> xs = [];
    private readonly List<float> ys = [];
    private readonly List<Vector4> band = [];
    private readonly List<Vector4> open = [];
    private readonly List<Vector4> active = [];

    /// <summary>Visible game windows this frame, as (minX, minY, maxX, maxY), for the Diagnostics page.</summary>
    public IReadOnlyList<Vector4> Windows => windows;
    /// <summary>Addon name of each entry in <see cref="Windows"/>.</summary>
    public IReadOnlyList<string> Names => names;

    /// <summary>Re-reads which game windows are on screen.</summary>
    public void Refresh(Vector2 origin, Vector2 size)
    {
        windows.Clear();
        names.Clear();
        CollectWindows(origin, size);
    }

    /// <summary>
    /// Splits <paramref name="screen"/> minus every hole into non-overlapping rectangles, all as
    /// (minX, minY, maxX, maxY). Writes the whole screen when there are no holes.
    /// </summary>
    public void Partition(Vector4 screen, List<Vector4> holes, List<Vector4> free)
    {
        free.Clear();
        if (holes.Count == 0)
        {
            free.Add(screen);
            return;
        }
        // Cut the screen at every window edge, then sweep it in horizontal bands: in each band the
        // free cells are merged into runs, and runs that continue a run of the band above extend it.
        xs.Clear();
        ys.Clear();
        xs.Add(screen.X); xs.Add(screen.Z);
        ys.Add(screen.Y); ys.Add(screen.W);
        foreach (var w in holes)
        {
            xs.Add(w.X); xs.Add(w.Z);
            ys.Add(w.Y); ys.Add(w.W);
        }
        SortUnique(xs);
        SortUnique(ys);

        open.Clear();
        for (var yi = 0; yi + 1 < ys.Count; yi++)
        {
            var y0 = ys[yi];
            var y1 = ys[yi + 1];
            var my = (y0 + y1) * 0.5f;

            active.Clear();
            foreach (var w in holes)
                if (my > w.Y && my < w.W) active.Add(w);

            band.Clear();
            var runStart = float.NaN;
            for (var xi = 0; xi + 1 < xs.Count; xi++)
            {
                var mx = (xs[xi] + xs[xi + 1]) * 0.5f;
                var covered = false;
                foreach (var w in active)
                {
                    if (mx > w.X && mx < w.Z)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered && float.IsNaN(runStart)) runStart = xs[xi];
                if (covered && !float.IsNaN(runStart))
                {
                    band.Add(new Vector4(runStart, y0, xs[xi], y1));
                    runStart = float.NaN;
                }
            }
            if (!float.IsNaN(runStart)) band.Add(new Vector4(runStart, y0, xs[^1], y1));

            // Extend runs from the band above that line up exactly; close the rest.
            for (var i = open.Count - 1; i >= 0; i--)
            {
                var o = open[i];
                var match = band.FindIndex(b => b.X == o.X && b.Z == o.Z);
                if (match >= 0)
                {
                    open[i] = o with { W = y1 };
                    band.RemoveAt(match);
                }
                else
                {
                    free.Add(o);
                    open.RemoveAt(i);
                }
            }
            open.AddRange(band);
        }
        free.AddRange(open);
    }

    private void CollectWindows(Vector2 origin, Vector2 size)
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return;

        ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        var count = Math.Min((int)list.Count, list.Entries.Length);
        var group = GroupManager.Instance();
        var inAlliance = group != null && group->MainGroup.IsAlliance;
        for (var i = 0; i < count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible) continue;

            var root = unit->RootNode;
            if (root == null || !root->IsVisible() || root->Color.A == 0) continue;

            var name = unit->NameString;
            if (Ignored.Contains(name)) continue;
            // The alliance lists stay loaded and "shown" with empty frames outside alliances.
            if (!inAlliance && name.StartsWith("_AllianceList", StringComparison.Ordinal)) continue;

            // The window's root is often far larger than what it draws (and stays "visible" while the
            // HUD fades it out), so measure only the nodes that actually put pixels on screen.
            var bounds = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            AddContent(ref unit->UldManager, ref bounds, 0);
            if (bounds.X >= bounds.Z || bounds.Y >= bounds.W) continue;

            var w = bounds.Z - bounds.X;
            var h = bounds.W - bounds.Y;
            if (w < 2f || h < 2f) continue;
            // Screen-sized layers (cutscene frames, overlays) aren't windows.
            if (w >= size.X * 0.9f && h >= size.Y * 0.9f) continue;

            var minX = Math.Clamp(origin.X + bounds.X, origin.X, origin.X + size.X);
            var minY = Math.Clamp(origin.Y + bounds.Y, origin.Y, origin.Y + size.Y);
            var maxX = Math.Clamp(origin.X + bounds.Z, origin.X, origin.X + size.X);
            var maxY = Math.Clamp(origin.Y + bounds.W, origin.Y, origin.Y + size.Y);
            if (maxX - minX < 1f || maxY - minY < 1f) continue;

            windows.Add(new Vector4(MathF.Floor(minX), MathF.Floor(minY), MathF.Ceiling(maxX), MathF.Ceiling(maxY)));
            names.Add(name);
        }
    }

    /// <summary>Grows <paramref name="bounds"/> by every visible image, text or frame node, components included.</summary>
    private static void AddContent(ref AtkUldManager uld, ref Vector4 bounds, int depth)
    {
        if (uld.NodeList == null || depth > 6) return;

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || !node->IsVisible()) continue;

            if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null && Shown(node, out _))
                    AddContent(ref component->UldManager, ref bounds, depth + 1);
                continue;
            }

            if (node->Type is not (NodeType.Image or NodeType.NineGrid or NodeType.Text or NodeType.Counter)) continue;
            if (node->Type == NodeType.Text && ((AtkTextNode*)node)->NodeText.Length == 0) continue;
            if (!Shown(node, out var scale)) continue;

            var w = node->Width * scale.X;
            var h = node->Height * scale.Y;
            if (w < 1f || h < 1f) continue;

            bounds.X = Math.Min(bounds.X, node->ScreenX);
            bounds.Y = Math.Min(bounds.Y, node->ScreenY);
            bounds.Z = Math.Max(bounds.Z, node->ScreenX + w);
            bounds.W = Math.Max(bounds.W, node->ScreenY + h);
        }
    }

    /// <summary>
    /// Whether a node and every parent above it are visible and not faded out, and the scale they
    /// add up to on screen.
    /// </summary>
    private static bool Shown(AtkResNode* node, out Vector2 scale)
    {
        scale = Vector2.One;
        var alpha = 1f;
        for (var n = node; n != null; n = n->ParentNode)
        {
            if (!n->IsVisible()) return false;
            alpha *= n->Color.A / 255f;
            if (alpha < 0.05f) return false;
            scale *= new Vector2(n->ScaleX, n->ScaleY);
        }
        return true;
    }

    private static void SortUnique(List<float> values)
    {
        values.Sort();
        var n = 0;
        for (var i = 0; i < values.Count; i++)
            if (n == 0 || values[i] != values[n - 1])
                values[n++] = values[i];
        values.RemoveRange(n, values.Count - n);
    }
}
