using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FateLimit.Models;

namespace FateLimit.Rendering;

/// <summary>Turns a world position into a screen position. Returns false when the point is behind the camera.</summary>
public delegate bool Projector(Vector3 world, out Vector2 screen);

/// <summary>Everything needed to draw one FATE's border for one frame.</summary>
public struct RingFrame
{
    public Vector3 Center;
    public float Radius;
    /// <summary>Ground height under each ring point, or null for a flat ring at the centre's height.</summary>
    public float[]? Heights;
    public int Segments;

    public BorderStyle Style;
    public StyleSettings Settings;

    /// <summary>Overall opacity, already including visibility fades.</summary>
    public float Alpha;
    /// <summary>0..1, how far toward the warning colour the part of the ring near the viewer leans.</summary>
    public float WarnBlend;
    public Vector4 WarnColor;

    /// <summary>The player: line widths, focus fade and warning tint are all measured from here.</summary>
    public Vector3 Viewer;
    /// <summary>Ring points farther than this from the viewer fade out. 0 disables the fade.</summary>
    public float FocusRadius;

    public float Time;
    public bool ReducedMotion;
    /// <summary>Screen height / 1080, so widths look the same at every resolution.</summary>
    public float PixelScale;
    public Projector Project;
}

/// <summary>
/// Draws a FATE's border in one of the <see cref="BorderStyle"/> looks. Lines are built as
/// feathered ribbons out of raw vertices rather than ImGui lines, so the glow has no beads where
/// segments meet and the colour can change smoothly along the ring.
/// </summary>
public sealed class BorderRenderer
{
    private const float Tau = MathF.PI * 2f;
    /// <summary>Distance, in yalms, at which a line is drawn at exactly its configured width.</summary>
    private const float ReferenceDistance = 18f;

    // Per-point scratch, grown as needed and reused every frame.
    private int count;
    private Vector3[] world = [];
    private float[] weight = [];
    private float[] widthScale = [];
    private Vector4[] tint = [];
    private float[] arc = [];
    private float[] segMul = [];

    // Projected rings: the edge itself plus a few offset copies some styles need.
    private readonly ProjectedRing edge = new();
    private readonly ProjectedRing ringA = new();
    private readonly ProjectedRing ringB = new();
    private readonly ProjectedRing ringC = new();

    private Vector2 uv;

    // The frame being drawn, kept so clipping can re-project points mid-ribbon.
    private Projector project = null!;
    private Vector3 center;
    private float radius;

    private sealed class ProjectedRing
    {
        public Vector2[] Screen = [];
        public bool[] Front = [];
        public float Inset;
        public float Lift;

        public void Ensure(int n)
        {
            if (Screen.Length >= n) return;
            Screen = new Vector2[n];
            Front = new bool[n];
        }
    }

    public void Draw(ImDrawListPtr dl, in RingFrame f)
    {
        if (f.Alpha <= 0.003f || f.Radius <= 0.5f || f.Segments < 8) return;

        uv = ImGui.GetFontTexUvWhitePixel();
        project = f.Project;
        center = f.Center;
        radius = f.Radius;
        Prepare(f);
        if (!AnyVisible()) return;

        switch (f.Style)
        {
            case BorderStyle.Barrier: DrawBarrier(dl, f); break;
            case BorderStyle.Dashes: DrawDashes(dl, f); break;
            case BorderStyle.Runes: DrawRunes(dl, f); break;
            case BorderStyle.Ember: DrawEmber(dl, f); break;
            default: DrawAether(dl, f); break;
        }
    }

    // ------------------------------------------------------------------
    // Shared geometry
    // ------------------------------------------------------------------

    private void Prepare(in RingFrame f)
    {
        count = f.Segments;
        if (world.Length < count)
        {
            world = new Vector3[count];
            weight = new float[count];
            widthScale = new float[count];
            tint = new Vector4[count];
            arc = new float[count];
            segMul = new float[count];
        }

        edge.Ensure(count);
        ringA.Ensure(count);
        ringB.Ensure(count);
        ringC.Ensure(count);

        var heights = f.Heights is { } h && h.Length == count ? h : null;
        var step = f.Radius * Tau / count;

        for (var i = 0; i < count; i++)
        {
            var theta = Tau * i / count;
            var y = heights?[i] ?? f.Center.Y;
            var p = new Vector3(f.Center.X + MathF.Cos(theta) * f.Radius, y + 0.06f, f.Center.Z + MathF.Sin(theta) * f.Radius);
            world[i] = p;
            arc[i] = step * i;

            var d = Vector3.Distance(p, f.Viewer);
            var focus = f.FocusRadius <= 0f ? 1f : 1f - SmoothStep(f.FocusRadius * 0.35f, f.FocusRadius, d);
            weight[i] = f.Alpha * focus;
            widthScale[i] = Math.Clamp(ReferenceDistance / Math.Max(d, 1f), 0.45f, 1.7f) * f.PixelScale;

            // The warning tint gathers on the part of the edge you're actually close to.
            var warn = f.WarnBlend * (1f - SmoothStep(4f, 26f, d));
            tint[i] = Mix(f.Settings.Color, f.WarnColor, warn);
        }

        ProjectRing(f, edge, 0f, 0f);
    }

    private bool AnyVisible()
    {
        for (var i = 0; i < count; i++)
            if (edge.Front[i] && weight[i] > 0.003f)
                return true;
        return false;
    }

    /// <summary>Projects a copy of the ring, pulled in by <paramref name="inset"/> and lifted by <paramref name="lift"/> yalms.</summary>
    private void ProjectRing(in RingFrame f, ProjectedRing ring, float inset, float lift)
    {
        ring.Inset = inset;
        ring.Lift = lift;
        for (var i = 0; i < count; i++)
            ring.Front[i] = f.Project(RingPoint(ring, i), out ring.Screen[i]);
    }

    /// <summary>World position of point <paramref name="i"/> of a projected ring.</summary>
    private Vector3 RingPoint(ProjectedRing ring, int i)
    {
        var p = world[i];
        if (ring.Inset != 0f)
        {
            var scale = (radius - ring.Inset) / radius;
            p = new Vector3(center.X + (p.X - center.X) * scale, p.Y, center.Z + (p.Z - center.Z) * scale);
        }

        p.Y += ring.Lift;
        return p;
    }

    /// <summary>Ground height at any angle, interpolated between ring points.</summary>
    private float HeightAt(in RingFrame f, float theta)
    {
        var t = theta / Tau * count;
        var i0 = (int)MathF.Floor(t);
        var frac = t - i0;
        i0 = ((i0 % count) + count) % count;
        var i1 = (i0 + 1) % count;
        return world[i0].Y + (world[i1].Y - world[i0].Y) * frac;
    }

    // ------------------------------------------------------------------
    // Styles
    // ------------------------------------------------------------------

    private void DrawAether(ImDrawListPtr dl, in RingFrame f)
    {
        var s = f.Settings;

        // A faint second ring just inside the edge, the double line of a magic circle.
        ProjectRing(f, ringA, 0.7f, 0f);
        Fill(segMul, 1f);
        Ribbon(dl, ringA, s.Thickness * 0.35f, 0.28f, 0f, false);

        Ribbon(dl, edge, s.Thickness, 0.95f, s.Glow, true);

        if (s.Animate && !f.ReducedMotion)
        {
            // Shimmer: bright pulses running around the ring.
            var t = f.Time * s.Speed;
            for (var i = 0; i < count; i++)
                segMul[i] = MathF.Pow(Math.Max(0f, MathF.Sin(arc[i] * 0.11f - t * 1.6f)), 14f);

            RibbonColored(dl, edge, s.Thickness * 0.9f, 0.8f, s.Glow * 0.6f, Lightened(0.65f));
        }
    }

    private void DrawBarrier(ImDrawListPtr dl, in RingFrame f)
    {
        var s = f.Settings;
        var h = Math.Max(s.Height, 0.3f);

        ProjectRing(f, ringA, 0f, h * 0.3f);
        ProjectRing(f, ringB, 0f, h);

        // The wall itself: brightest at the foot, gone at the top.
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            if (weight[i] <= 0.003f && weight[j] <= 0.003f) continue;
            if (!edge.Front[i] || !edge.Front[j] || !ringA.Front[i] || !ringA.Front[j]) continue;

            var c0 = tint[i];
            var c1 = tint[j];
            Quad(dl, edge.Screen[i], edge.Screen[j], ringA.Screen[j], ringA.Screen[i],
                Pack(c0, weight[i] * 0.42f), Pack(c1, weight[j] * 0.42f), Pack(c1, weight[j] * 0.2f), Pack(c0, weight[i] * 0.2f));

            if (!ringB.Front[i] || !ringB.Front[j]) continue;
            Quad(dl, ringA.Screen[i], ringA.Screen[j], ringB.Screen[j], ringB.Screen[i],
                Pack(c0, weight[i] * 0.2f), Pack(c1, weight[j] * 0.2f), 0, 0);
        }

        // Light streaks running up the wall.
        var spacing = Math.Max(s.Spacing, 0.5f);
        var streaks = Math.Max(8, (int)MathF.Round(f.Radius * Tau / spacing));
        for (var k = 0; k < streaks; k++)
        {
            var i = (int)((long)k * count / streaks);
            if (!edge.Front[i] || !ringB.Front[i]) continue;

            var a = edge.Screen[i];
            var b = Vector2.Lerp(a, ringB.Screen[i], 0.85f);
            var w = Math.Max(1f, 1.4f * widthScale[i]);
            var n = Perp(b - a) * (w * 0.5f);
            Quad(dl, a - n, a + n, b + n, b - n,
                Pack(Lighten(tint[i], 0.3f), weight[i] * 0.4f), Pack(Lighten(tint[i], 0.3f), weight[i] * 0.4f), 0, 0);
        }

        // A soft band of light sweeping up the wall.
        if (s.Animate && !f.ReducedMotion)
        {
            var phase = f.Time * s.Speed * 0.3f % 1f;
            ProjectRing(f, ringC, 0f, h * phase);
            Fill(segMul, (1f - phase) * 0.6f);
            RibbonColored(dl, ringC, s.Thickness * 0.7f, 0.6f, 0.8f, Lightened(0.35f));
        }

        Fill(segMul, 1f);
        Ribbon(dl, ringB, s.Thickness * 0.4f, 0.22f, 0f, false);
        Ribbon(dl, edge, s.Thickness, 0.95f, s.Glow, true);
    }

    private void DrawDashes(ImDrawListPtr dl, in RingFrame f)
    {
        var s = f.Settings;
        var dash = Math.Max(s.Spacing, 0.4f);
        var period = dash * 1.75f;
        var offset = s.Animate && !f.ReducedMotion ? f.Time * s.Speed * 1.5f : 0f;
        var step = f.Radius * Tau / count;

        // A barely-there line underneath keeps the circle readable between dashes.
        Fill(segMul, 1f);
        Ribbon(dl, edge, s.Thickness * 0.4f, 0.16f, 0f, false);

        for (var i = 0; i < count; i++)
        {
            var mid = arc[i] + step * 0.5f + offset;
            var phase = mid / period % 1f;
            segMul[i] = phase < dash / period ? 1f : 0f;
        }

        Ribbon(dl, edge, s.Thickness, 0.95f, s.Glow, true);
    }

    private void DrawRunes(ImDrawListPtr dl, in RingFrame f)
    {
        var s = f.Settings;

        // The thread linking the runes.
        Fill(segMul, 1f);
        Ribbon(dl, edge, s.Thickness, 0.45f, s.Glow * 0.5f, false);

        var spacing = Math.Max(s.Spacing, 1f);
        var runes = Math.Max(6, (int)MathF.Round(f.Radius * Tau / spacing));
        var animate = s.Animate && !f.ReducedMotion;
        var t = f.Time * s.Speed;
        var size = 0.32f;

        for (var k = 0; k < runes; k++)
        {
            var theta = Tau * k / runes;
            var idx = (int)((long)k * count / runes) % count;
            var w = weight[idx];
            if (w <= 0.01f) continue;

            var ground = new Vector3(
                f.Center.X + MathF.Cos(theta) * f.Radius,
                HeightAt(f, theta),
                f.Center.Z + MathF.Sin(theta) * f.Radius);

            var lift = Math.Max(s.Height, 0.4f) * (animate ? 1f + 0.07f * MathF.Sin(t * 1.7f + k * 1.3f) : 1f);
            var gem = ground + new Vector3(0, lift, 0);

            if (!f.Project(ground, out var g) || !f.Project(gem, out var c)
                || !f.Project(gem + new Vector3(0, size, 0), out var top))
                continue;

            var col = tint[idx];
            var hh = Math.Clamp(Vector2.Distance(top, c), 2.5f, 40f);
            var spin = animate ? 0.3f + 0.7f * MathF.Abs(MathF.Cos(t * 1.2f + k * 0.7f)) : 0.75f;
            var hw = hh * 0.62f * spin;

            // Pillar of light from the ground up to the gem.
            var pw = Math.Max(1f, 1.6f * widthScale[idx]);
            var n = Perp(c - g) * (pw * 0.5f);
            Quad(dl, g - n, g + n, c + n, c - n, Pack(col, w * 0.55f), Pack(col, w * 0.55f), Pack(col, w * 0.05f), Pack(col, w * 0.05f));

            dl.AddCircleFilled(g, Math.Max(1.5f, 2.6f * widthScale[idx]), Pack(Lighten(col, 0.4f), w * 0.6f), 10);

            if (s.Glow > 0f)
            {
                dl.AddCircleFilled(c, hh * 2.4f, Pack(col, w * 0.08f * s.Glow), 20);
                dl.AddCircleFilled(c, hh * 1.5f, Pack(col, w * 0.14f * s.Glow), 16);
            }

            var pTop = new Vector2(c.X, c.Y - hh);
            var pRight = new Vector2(c.X + hw, c.Y);
            var pBottom = new Vector2(c.X, c.Y + hh);
            var pLeft = new Vector2(c.X - hw, c.Y);
            Quad(dl, pTop, pRight, pBottom, pLeft,
                Pack(Lighten(col, 0.7f), w), Pack(col, w * 0.9f), Pack(Darken(col, 0.35f), w * 0.9f), Pack(Lighten(col, 0.25f), w * 0.9f));
            dl.AddQuad(pTop, pRight, pBottom, pLeft, Pack(Lighten(col, 0.6f), w * 0.85f), 1.1f);
        }
    }

    private void DrawEmber(ImDrawListPtr dl, in RingFrame f)
    {
        var s = f.Settings;
        var band = Math.Clamp(s.Height, 0.3f, f.Radius * 0.6f);
        var animate = s.Animate && !f.ReducedMotion;
        var t = f.Time * s.Speed;

        ProjectRing(f, ringA, band * 0.35f, 0f);
        ProjectRing(f, ringB, band, 0f);

        for (var i = 0; i < count; i++)
        {
            segMul[i] = animate
                ? 0.8f + 0.14f * MathF.Sin(arc[i] * 0.7f - t * 2.2f) + 0.08f * MathF.Sin(arc[i] * 1.9f + t * 1.3f)
                : 0.9f;
        }

        // The band, hot at the edge and cooling toward the middle of the FATE.
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            if (weight[i] <= 0.003f && weight[j] <= 0.003f) continue;
            if (!edge.Front[i] || !edge.Front[j] || !ringA.Front[i] || !ringA.Front[j]) continue;

            var a0 = weight[i] * segMul[i];
            var a1 = weight[j] * segMul[j];
            Quad(dl, edge.Screen[i], edge.Screen[j], ringA.Screen[j], ringA.Screen[i],
                Pack(tint[i], a0 * 0.4f), Pack(tint[j], a1 * 0.4f), Pack(tint[j], a1 * 0.16f), Pack(tint[i], a0 * 0.16f));

            if (!ringB.Front[i] || !ringB.Front[j]) continue;
            Quad(dl, ringA.Screen[i], ringA.Screen[j], ringB.Screen[j], ringB.Screen[i],
                Pack(tint[i], a0 * 0.16f), Pack(tint[j], a1 * 0.16f), 0, 0);
        }

        Ribbon(dl, edge, s.Thickness, 1f, s.Glow, true);
    }

    // ------------------------------------------------------------------
    // Markers
    // ------------------------------------------------------------------

    /// <summary>
    /// A warning gem hovering over the point of the edge closest to the viewer, with a ring
    /// pulsing on the ground beneath it.
    /// </summary>
    public void DrawEdgeMarker(ImDrawListPtr dl, in RingFrame f, Vector4 color, float alpha)
    {
        if (alpha <= 0.01f) return;
        uv = ImGui.GetFontTexUvWhitePixel();

        var toViewer = new Vector2(f.Viewer.X - f.Center.X, f.Viewer.Z - f.Center.Z);
        if (toViewer.LengthSquared() < 0.01f) return;

        var theta = MathF.Atan2(toViewer.Y, toViewer.X);
        if (theta < 0) theta += Tau;

        var dir = Vector2.Normalize(toViewer);
        var ground = new Vector3(f.Center.X + dir.X * f.Radius, HeightAtSafe(f, theta), f.Center.Z + dir.Y * f.Radius);
        var pulse = f.ReducedMotion ? 0.5f : 0.5f + 0.5f * MathF.Sin(f.Time * 5f);

        // Ground ring, drawn as a short projected circle so it lies flat on the floor.
        const int pts = 24;
        Span<Vector2> ring = stackalloc Vector2[pts];
        var radius = 0.7f + 0.25f * pulse;
        var ok = true;
        for (var i = 0; i < pts && ok; i++)
        {
            var a = Tau * i / pts;
            ok = f.Project(ground + new Vector3(MathF.Cos(a) * radius, 0.05f, MathF.Sin(a) * radius), out ring[i]);
        }

        if (ok)
        {
            for (var i = 0; i < pts; i++)
                dl.AddLine(ring[i], ring[(i + 1) % pts], Pack(color, alpha * (0.5f + 0.4f * pulse)), 2f * f.PixelScale);
        }

        var gem = ground + new Vector3(0, 1.5f + (f.ReducedMotion ? 0f : 0.1f * MathF.Sin(f.Time * 2.4f)), 0);
        if (!f.Project(ground, out var g) || !f.Project(gem, out var c) || !f.Project(gem + new Vector3(0, 0.4f, 0), out var top))
            return;

        var hh = Math.Clamp(Vector2.Distance(top, c), 7f, 26f);
        var hw = hh * 0.8f;

        dl.AddLine(g, c, Pack(color, alpha * 0.4f), 1.5f);
        dl.AddCircleFilled(c, hh * 1.9f, Pack(color, alpha * (0.08f + 0.1f * pulse)), 24);

        var pTop = new Vector2(c.X, c.Y - hh);
        var pRight = new Vector2(c.X + hw, c.Y);
        var pBottom = new Vector2(c.X, c.Y + hh);
        var pLeft = new Vector2(c.X - hw, c.Y);
        Quad(dl, pTop, pRight, pBottom, pLeft,
            Pack(Lighten(color, 0.45f), alpha), Pack(color, alpha * 0.95f), Pack(Darken(color, 0.3f), alpha * 0.95f), Pack(color, alpha * 0.95f));
        dl.AddQuad(pTop, pRight, pBottom, pLeft, Pack(Lighten(color, 0.7f), alpha), 1.5f);

        // Exclamation mark, built from shapes so it scales with the gem.
        var ink = Pack(new Vector4(0.08f, 0.05f, 0.04f, 1f), alpha);
        var barW = hh * 0.14f;
        dl.AddRectFilled(new Vector2(c.X - barW, c.Y - hh * 0.52f), new Vector2(c.X + barW, c.Y + hh * 0.14f), ink, barW);
        dl.AddCircleFilled(new Vector2(c.X, c.Y + hh * 0.42f), barW * 1.15f, ink, 10);
    }

    /// <summary>Ground height straight from the frame, for drawing that doesn't go through <see cref="Prepare"/>.</summary>
    private static float HeightAtSafe(in RingFrame f, float theta)
    {
        if (f.Heights is not { Length: > 1 } h) return f.Center.Y;
        var t = theta / Tau * h.Length;
        var i0 = (int)MathF.Floor(t);
        var frac = t - i0;
        i0 = ((i0 % h.Length) + h.Length) % h.Length;
        return h[i0] + (h[(i0 + 1) % h.Length] - h[i0]) * frac;
    }

    // ------------------------------------------------------------------
    // Ribbons
    // ------------------------------------------------------------------

    /// <summary>The ring's tint, lightened, in a buffer reused across frames.</summary>
    private Vector4[] Lightened(float lighten)
    {
        if (shimmerTint.Length < count) shimmerTint = new Vector4[count];
        for (var i = 0; i < count; i++)
            shimmerTint[i] = Lighten(tint[i], lighten);
        return shimmerTint;
    }

    private Vector4[] shimmerTint = [];

    private void Ribbon(ImDrawListPtr dl, ProjectedRing ring, float width, float alpha, float glow, bool hotCore)
    {
        RibbonColored(dl, ring, width, alpha, glow, tint);

        if (hotCore)
        {
            if (coreTint.Length < count) coreTint = new Vector4[count];
            for (var i = 0; i < count; i++)
                coreTint[i] = Lighten(tint[i], 0.55f);
            RibbonColored(dl, ring, width * 0.35f, alpha * 0.6f, 0f, coreTint);
        }
    }

    private Vector4[] coreTint = [];

    /// <summary>
    /// A screen-space strip along the ring: a solid core of <paramref name="width"/> pixels with a
    /// feathered fringe either side, and an optional wide glow. Per-segment strength comes from
    /// <see cref="segMul"/>.
    /// </summary>
    private void RibbonColored(ImDrawListPtr dl, ProjectedRing ring, float width, float alpha, float glow, Vector4[] colors)
    {
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            var m = segMul[i];
            if (m <= 0.003f) continue;

            var ai = weight[i] * alpha * m;
            var aj = weight[j] * alpha * m;
            if (ai <= 0.003f && aj <= 0.003f) continue;

            Vector2 a, b, na, nb;
            if (ring.Front[i] && ring.Front[j])
            {
                a = ring.Screen[i];
                b = ring.Screen[j];
                na = Normal(ring, i);
                nb = Normal(ring, j);
            }
            else if (ring.Front[i] || ring.Front[j])
            {
                // One end is behind the camera: walk the segment back to where it leaves the view.
                if (!ClipSegment(ring, i, j, out a, out b)) continue;
                na = nb = Perp(b - a);
            }
            else
            {
                continue;
            }

            if (Vector2.DistanceSquared(a, b) > 4e6f) continue;

            var wa = Math.Max(width * widthScale[i], 0.8f) * 0.5f;
            var wb = Math.Max(width * widthScale[j], 0.8f) * 0.5f;
            var ci = colors[i];
            var cj = colors[j];

            if (glow > 0.01f)
            {
                var ga = wa * (1f + 5f * glow);
                var gb = wb * (1f + 5f * glow);
                var gai = Pack(ci, ai * 0.22f * glow);
                var gaj = Pack(cj, aj * 0.22f * glow);
                Quad(dl, a + na * wa, b + nb * wb, b + nb * gb, a + na * ga, gai, gaj, 0, 0);
                Quad(dl, a - na * wa, b - nb * wb, b - nb * gb, a - na * ga, gai, gaj, 0, 0);
            }

            // Core plus a one-pixel feather on each side stands in for anti-aliasing.
            var pi = Pack(ci, ai);
            var pj = Pack(cj, aj);
            Quad(dl, a + na * wa, b + nb * wb, b - nb * wb, a - na * wa, pi, pj, pj, pi);
            Quad(dl, a + na * wa, b + nb * wb, b + nb * (wb + 1f), a + na * (wa + 1f), pi, pj, 0, 0);
            Quad(dl, a - na * wa, b - nb * wb, b - nb * (wb + 1f), a - na * (wa + 1f), pi, pj, 0, 0);
        }
    }

    /// <summary>Screen-space normal at a ring point, averaged over its neighbours so joints stay smooth.</summary>
    private Vector2 Normal(ProjectedRing ring, int i)
    {
        var prev = (i - 1 + count) % count;
        var next = (i + 1) % count;
        var from = ring.Front[prev] ? ring.Screen[prev] : ring.Screen[i];
        var to = ring.Front[next] ? ring.Screen[next] : ring.Screen[i];
        return Perp(to - from);
    }

    /// <summary>
    /// For a segment with one end behind the camera, finds by bisection the last point still in
    /// front and returns the visible part.
    /// </summary>
    private bool ClipSegment(ProjectedRing ring, int i, int j, out Vector2 a, out Vector2 b)
    {
        a = b = default;
        var (front, back) = ring.Front[i] ? (i, j) : (j, i);
        var pf = RingPoint(ring, front);
        var pb = RingPoint(ring, back);

        float lo = 0f, hi = 1f;
        for (var k = 0; k < 7; k++)
        {
            var mid = (lo + hi) * 0.5f;
            if (project(Vector3.Lerp(pf, pb, mid), out _)) lo = mid;
            else hi = mid;
        }

        if (lo <= 0f || !project(Vector3.Lerp(pf, pb, lo), out var cut)) return false;

        if (front == i) { a = ring.Screen[i]; b = cut; }
        else { a = cut; b = ring.Screen[j]; }
        return true;
    }

    // ------------------------------------------------------------------
    // Primitives
    // ------------------------------------------------------------------

    private void Quad(ImDrawListPtr dl, Vector2 a, Vector2 b, Vector2 c, Vector2 d, uint ca, uint cb, uint cc, uint cd)
    {
        if ((ca | cb | cc | cd) >> 24 == 0) return;
        dl.PrimReserve(6, 6);
        dl.PrimVtx(a, uv, ca);
        dl.PrimVtx(b, uv, cb);
        dl.PrimVtx(c, uv, cc);
        dl.PrimVtx(a, uv, ca);
        dl.PrimVtx(c, uv, cc);
        dl.PrimVtx(d, uv, cd);
    }

    private static void Fill(float[] values, float value) => Array.Fill(values, value);

    private static Vector2 Perp(Vector2 v)
    {
        var len = v.Length();
        return len < 1e-4f ? new Vector2(0, 1) : new Vector2(-v.Y / len, v.X / len);
    }

    /// <summary>Packs a colour as ImGui's ABGR, with its alpha scaled by <paramref name="alpha"/>.</summary>
    public static uint Pack(Vector4 c, float alpha)
    {
        var a = Math.Clamp(c.W * alpha, 0f, 1f);
        return ((uint)(a * 255f + 0.5f) << 24)
               | ((uint)(Math.Clamp(c.Z, 0f, 1f) * 255f + 0.5f) << 16)
               | ((uint)(Math.Clamp(c.Y, 0f, 1f) * 255f + 0.5f) << 8)
               | (uint)(Math.Clamp(c.X, 0f, 1f) * 255f + 0.5f);
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        if (Math.Abs(edge1 - edge0) < 1e-6f) return x >= edge1 ? 1f : 0f;
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));
    private static Vector4 Lighten(Vector4 c, float t) => new(c.X + (1 - c.X) * t, c.Y + (1 - c.Y) * t, c.Z + (1 - c.Z) * t, c.W);
    private static Vector4 Darken(Vector4 c, float t) => new(c.X * (1 - t), c.Y * (1 - t), c.Z * (1 - t), c.W);
}
