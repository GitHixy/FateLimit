using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FateLimit.Rendering;

/// <summary>
/// The on-screen edge warning: a dark band that fades out to both sides, framed by thin rules,
/// with a title and a distance line - the same shape as the game's own area banners.
/// </summary>
public static class Banner
{
    public static void Draw(ImDrawListPtr dl, Vector2 center, float scale, string title, string detail,
        Vector4 accent, float alpha, float time, bool reducedMotion)
    {
        if (alpha <= 0.01f) return;

        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        var titleSize = baseSize * 1.45f * scale;
        var detailSize = baseSize * 0.95f * scale;

        // Glyph advances scale linearly with size, so measuring at the base size is enough.
        var titleExtent = ImGui.CalcTextSize(title) * (titleSize / baseSize);
        var detailExtent = ImGui.CalcTextSize(detail) * (detailSize / baseSize);

        var halfWidth = Math.Max(titleExtent.X, detailExtent.X) * 0.5f + 110f * scale;
        var height = titleExtent.Y + detailExtent.Y + 18f * scale;
        var top = center.Y - height * 0.5f;
        var bottom = center.Y + height * 0.5f;

        var pulse = reducedMotion ? 0f : 0.5f + 0.5f * MathF.Sin(time * 4f);
        var ink = new Vector4(0.03f, 0.04f, 0.07f, 1f);

        // Band: solid in the middle, fading to nothing at both ends.
        var mid = BorderRenderer.Pack(ink, alpha * 0.62f);
        dl.AddRectFilledMultiColor(new Vector2(center.X - halfWidth, top), new Vector2(center.X, bottom), 0, mid, mid, 0);
        dl.AddRectFilledMultiColor(new Vector2(center.X, top), new Vector2(center.X + halfWidth, bottom), mid, 0, 0, mid);

        // Rules above and below in the accent colour, brightest at the centre.
        var rule = BorderRenderer.Pack(accent, alpha * (0.75f + 0.25f * pulse));
        foreach (var y in new[] { top, bottom - 1.5f * scale })
        {
            dl.AddRectFilledMultiColor(new Vector2(center.X - halfWidth, y), new Vector2(center.X, y + 1.5f * scale), 0, rule, rule, 0);
            dl.AddRectFilledMultiColor(new Vector2(center.X, y), new Vector2(center.X + halfWidth, y + 1.5f * scale), rule, 0, 0, rule);
        }

        // Diamonds flanking the title, the game's favourite ornament.
        var titleY = top + 7f * scale;
        var gemY = titleY + titleExtent.Y * 0.5f;
        var gemX = titleExtent.X * 0.5f + 18f * scale;
        var gemR = 5f * scale;
        var gem = BorderRenderer.Pack(accent, alpha);
        foreach (var x in new[] { center.X - gemX, center.X + gemX })
        {
            dl.AddQuadFilled(new Vector2(x, gemY - gemR), new Vector2(x + gemR, gemY), new Vector2(x, gemY + gemR), new Vector2(x - gemR, gemY), gem);
        }

        Outlined(dl, font, titleSize, new Vector2(center.X - titleExtent.X * 0.5f, titleY),
            Lighten(accent, 0.35f), alpha, title, 1.4f * scale);
        Outlined(dl, font, detailSize, new Vector2(center.X - detailExtent.X * 0.5f, titleY + titleExtent.Y + 2f * scale),
            new Vector4(0.91f, 0.88f, 0.83f, 1f), alpha * 0.95f, detail, 1.1f * scale);
    }

    private static void Outlined(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, Vector4 color, float alpha, string text, float outline)
    {
        var shadow = BorderRenderer.Pack(new Vector4(0, 0, 0, 1), alpha * 0.85f);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            dl.AddText(font, size, pos + new Vector2(dx, dy) * outline, shadow, text);
        }

        dl.AddText(font, size, pos, BorderRenderer.Pack(color, alpha), text);
    }

    private static Vector4 Lighten(Vector4 c, float t) => new(c.X + (1 - c.X) * t, c.Y + (1 - c.Y) * t, c.Z + (1 - c.Z) * t, c.W);
}
