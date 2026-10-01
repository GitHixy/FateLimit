using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Fates;
using FateLimit.Rendering;
using FateLimit.Services;

namespace FateLimit.UI;

/// <summary>
/// A small stage inside the settings window: a FATE seen from a slowly orbiting camera while a
/// stand-in for the player walks out to its edge and back. It runs the same renderer and the same
/// visibility rules as the world, so what you see here is what you'll get in game.
/// </summary>
public sealed class PreviewCanvas
{
    private const float Radius = FateTracker.PracticeRadius;
    private const ushort PreviewId = ushort.MaxValue - 1;

    private readonly Plugin plugin;
    private readonly BorderRenderer renderer = new();
    private readonly FateInfo fate = new(PreviewId, "Preview", Vector3.Zero, Radius, FateState.Running, 0, 0, 0, true);

    private Matrix4x4 viewProjection;
    private Vector2 canvasMin;
    private Vector2 canvasSize;
    private float fade;
    private readonly Projector project;

    public PreviewCanvas(Plugin plugin)
    {
        this.plugin = plugin;
        project = Project;
    }

    public void Draw(string id, Vector2 size)
    {
        var config = plugin.Configuration;
        var width = size.X > 0 ? size.X : ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        canvasMin = p;
        canvasSize = new Vector2(width, size.Y);
        var max = p + canvasSize;

        ImGui.InvisibleButton(id, canvasSize);

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, max, Theme.U32(new Vector4(0.03f, 0.05f, 0.08f, 1f)), Theme.Radius);
        dl.AddRectFilledMultiColor(p, new Vector2(max.X, p.Y + size.Y * 0.55f),
            Theme.U32(Theme.Crystal, 0.07f), Theme.U32(Theme.Crystal, 0.07f), Theme.U32(Theme.Crystal, 0f), Theme.U32(Theme.Crystal, 0f));
        dl.PushClipRect(p, max, true);

        var reduced = plugin.ReducedMotion;
        var time = (float)plugin.Now;

        // Camera orbiting the FATE, a little above head height.
        var yaw = reduced ? 0.6f : time * 0.12f;
        var eye = new Vector3(MathF.Cos(yaw) * 36f, 17f, MathF.Sin(yaw) * 36f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0, -2f, 0), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.6f, canvasSize.X / Math.Max(canvasSize.Y, 1f), 0.3f, 400f);
        viewProjection = view * proj;

        DrawGround(dl);

        // The stand-in walks out along the side facing the camera, past the edge, and back.
        var walk = 0.62f + 0.46f * MathF.Sin(time * 0.45f);
        var heading = yaw + 0.55f;
        var player = new Vector3(MathF.Cos(heading) * Radius * walk, 0f, MathF.Sin(heading) * Radius * walk);

        var evaluated = FateLogic.Evaluate(config, fate, player, 0, PreviewId);
        var target = evaluated.Visibility;
        var rate = Math.Clamp(ImGui.GetIO().DeltaTime * (target > fade ? 7f : 4f), 0f, 1f);
        fade += (target - fade) * rate;

        var frame = new RingFrame
        {
            Center = Vector3.Zero,
            Radius = Radius,
            Heights = null,
            Segments = TerrainService.SegmentsFor(Radius),
            Style = config.Style,
            Settings = config.ActiveStyle,
            Alpha = fade * config.Opacity,
            WarnBlend = config.WarnColorShift ? evaluated.Closeness : 0f,
            WarnColor = config.WarnColor,
            Viewer = player,
            FocusRadius = config.FocusRadius,
            Time = time,
            ReducedMotion = reduced,
            PixelScale = Math.Clamp(canvasSize.Y / 420f, 0.5f, 1f),
            Project = project,
        };

        renderer.Draw(dl, frame);

        DrawPlayer(dl, player);

        if (config.ShowEdgeMarker)
            renderer.DrawEdgeMarker(dl, frame, Overlay.MarkerColor(config), evaluated.Closeness * config.Opacity);

        if (config.ShowBanner && evaluated.Closeness > 0f)
        {
            var (title, detail) = Overlay.BannerText(evaluated);
            Banner.Draw(dl, new Vector2(p.X + canvasSize.X * 0.5f, p.Y + 34f), 0.62f, title, detail,
                config.WarnColor, Math.Clamp(evaluated.Closeness * 1.6f, 0f, 1f) * config.Opacity, time, reduced);
        }

        // Caption: distance to the edge, bottom left.
        var caption = evaluated.Inside
            ? $"{evaluated.EdgeDistance:0.0} y to the edge"
            : $"{-evaluated.EdgeDistance:0.0} y outside";
        dl.AddText(new Vector2(p.X + 10, max.Y - ImGui.GetTextLineHeight() - 8), Theme.U32(Theme.TextMuted), caption);

        dl.PopClipRect();
        dl.AddRect(p, max, Theme.U32(Theme.Line), Theme.Radius, ImDrawFlags.None, 1f);
    }

    private bool Project(Vector3 world, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (clip.W < 0.3f)
        {
            screen = default;
            return false;
        }

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        screen = new Vector2(
            canvasMin.X + (ndc.X + 1f) * 0.5f * canvasSize.X,
            canvasMin.Y + (1f - ndc.Y) * 0.5f * canvasSize.Y);
        return true;
    }

    /// <summary>A faint compass of rings and spokes so the scene has a floor.</summary>
    private void DrawGround(ImDrawListPtr dl)
    {
        var line = Theme.U32(Theme.Line, 0.55f);
        foreach (var r in new[] { 8f, 16f, 24f, 32f })
        {
            const int n = 64;
            for (var i = 0; i < n; i++)
            {
                var a0 = MathF.PI * 2f * i / n;
                var a1 = MathF.PI * 2f * (i + 1) / n;
                if (Project(new Vector3(MathF.Cos(a0) * r, 0, MathF.Sin(a0) * r), out var s0)
                    && Project(new Vector3(MathF.Cos(a1) * r, 0, MathF.Sin(a1) * r), out var s1))
                    dl.AddLine(s0, s1, line, 1f);
            }
        }

        for (var k = 0; k < 8; k++)
        {
            var a = MathF.PI * 2f * k / 8;
            if (Project(new Vector3(MathF.Cos(a) * 4f, 0, MathF.Sin(a) * 4f), out var s0)
                && Project(new Vector3(MathF.Cos(a) * 34f, 0, MathF.Sin(a) * 34f), out var s1))
                dl.AddLine(s0, s1, Theme.U32(Theme.LineSoft, 0.8f), 1f);
        }

        // The FATE's own map marker at the centre.
        if (Project(Vector3.Zero, out var c))
            Theme.DrawDiamond(dl, c, 6f, Theme.U32(Theme.Gold, 0.35f), Theme.U32(Theme.Gold, 0.8f), 1.2f);
    }

    private void DrawPlayer(ImDrawListPtr dl, Vector3 player)
    {
        if (!Project(player, out var feet) || !Project(player + new Vector3(0, 1.8f, 0), out var head)) return;

        const int n = 20;
        Span<Vector2> ring = stackalloc Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var a = MathF.PI * 2f * i / n;
            if (!Project(player + new Vector3(MathF.Cos(a) * 0.7f, 0, MathF.Sin(a) * 0.7f), out ring[i])) return;
        }

        for (var i = 0; i < n; i++)
            dl.AddLine(ring[i], ring[(i + 1) % n], Theme.U32(Theme.GoldBright, 0.8f), 1.5f);

        dl.AddLine(feet, head, Theme.U32(Theme.GoldBright, 0.9f), 2.5f);
        dl.AddCircleFilled(head, 4.5f, Theme.U32(Theme.GoldBright), 12);
    }
}
