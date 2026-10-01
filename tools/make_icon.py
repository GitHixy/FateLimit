"""
Generates FateLimit's icon (icon.png, 512x512) from one set of constants.

It shares the panel language of LootView and HitSpark - the deep-navy glass and brass frame
every window of the three plugins is built from - so they read as siblings. The subject is
FateLimit's own: a FATE's border seen in perspective, a glowing barrier rising from the ground,
with the warning gem hovering where you're about to step out.

Run from the repository root:  python tools/make_icon.py
"""
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# ---------------------------------------------------------------- palette ------
# Panel colours straight from src/UI/Theme.cs.
INK_TOP = (0x18, 0x21, 0x33)
INK_BOTTOM = (0x06, 0x09, 0x10)
CRYSTAL = (0x6F, 0xB8, 0xE0)
GOLD = (0xD6, 0xB0, 0x68)
GOLD_BRIGHT = (0xF2, 0xD7, 0x96)

# The ring: the FATE ember style, warming to the warning red where the gem hovers.
EMBER = (0xFF, 0x9E, 0x40)
EMBER_HOT = (0xFF, 0xDC, 0x9A)
WARN = (0xEE, 0x54, 0x47)
WHITE_HOT = (0xFF, 0xFB, 0xEE)

# ---------------------------------------------------------------- geometry -----
SIZE = 512
CORNER = 104
FRAME_INSET = 36
FRAME_RADIUS = 72
FRAME_WIDTH = 3.5
BRACKET = 34

# The ring is an ellipse: a circle on the ground seen from above and in front.
RCX, RCY = 256, 312
RX, RY = 168, 72
WALL = 74            # how high the barrier rises, in pixels
WALL_LAYERS = 40

# Where on the ring the warning sits, in degrees (0 = right, 90 = nearest the viewer).
WARN_ANGLE = 58
GEM_LIFT = 92
GEM_R = 40


# ================================================================== helpers ====
def layer(n):
    return Image.new('RGBA', (n, n), (0, 0, 0, 0))


def fade(img, alpha):
    """Scales a layer's alpha channel, so it composites at the intended opacity."""
    r, g, b, a = img.split()
    return Image.merge('RGBA', (r, g, b, a.point(lambda v: int(v * alpha))))


def mix(a, b, t):
    return tuple(int(a[i] * (1 - t) + b[i] * t) for i in range(3))


def ring_point(deg, lift=0.0):
    a = math.radians(deg)
    return RCX + math.cos(a) * RX, RCY + math.sin(a) * RY - lift


def warn_weight(deg):
    """How much of the warning colour a point of the ring carries: strongest by the gem."""
    d = abs((deg - WARN_ANGLE + 180) % 360 - 180)
    return max(0.0, 1.0 - d / 70.0) ** 1.6


def corner_brackets():
    """L-shaped brass corner ornaments, hugging the frame's rounded corners (as LootView)."""
    lo, hi = FRAME_INSET, SIZE - FRAME_INSET
    out = []
    for (x, y, dx, dy) in [(lo, lo, 1, 1), (hi, lo, -1, 1), (lo, hi, 1, -1), (hi, hi, -1, -1)]:
        ox = x + dx * FRAME_RADIUS * 0.30
        oy = y + dy * FRAME_RADIUS * 0.30
        out.append(((ox, oy + dy * BRACKET), (ox, oy), (ox + dx * BRACKET, oy)))
    return out


def draw_ring(d, k, width, lift=0.0, alpha=1.0, color_fn=None, back=None):
    """Strokes the ellipse as short segments so the colour can change along it.
    `back` = True draws only the far half, False only the near half, None both."""
    steps = 360
    for i in range(steps):
        a0, a1 = 360 * i / steps, 360 * (i + 1) / steps
        far = math.sin(math.radians((a0 + a1) / 2)) < 0
        if back is not None and far != back:
            continue
        c = color_fn(a0) if color_fn else EMBER
        x0, y0 = ring_point(a0, lift)
        x1, y1 = ring_point(a1 + 0.8, lift)  # overlap a touch so joints don't show
        d.line([(x0 * k, y0 * k), (x1 * k, y1 * k)], fill=c + (int(255 * alpha),), width=max(1, int(width * k)))


# ================================================================== render =====
def render_png(path, size=SIZE, ss=4):
    """Renders at `ss` times the target size, then downsamples for clean edges."""
    n = size * ss
    k = n / SIZE

    # --- panel: navy gradient, a cool top glow and the ring's warm light -----
    ramp = np.linspace(0.0, 1.0, n, dtype=np.float32)[:, None]
    base = np.zeros((n, n, 3), dtype=np.float32)
    for c in range(3):
        base[:, :, c] = INK_TOP[c] * (1 - ramp) + INK_BOTTOM[c] * ramp

    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    top_glow = np.clip(1.0 - ys / (n * 0.5), 0, 1) ** 2 * 0.30
    pool = np.clip(1.0 - np.sqrt(((xs - RCX * k) / (RX * 1.25 * k)) ** 2 + ((ys - RCY * k) / (RY * 2.2 * k)) ** 2), 0, 1) ** 1.6
    for c in range(3):
        base[:, :, c] += CRYSTAL[c] * top_glow
        base[:, :, c] += EMBER[c] * pool * 0.30

    panel = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    panel_mask = Image.new('L', (n, n), 0)
    ImageDraw.Draw(panel_mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=CORNER * k, fill=255)
    panel.putalpha(panel_mask)
    out = panel

    # --- brass frame -------------------------------------------------------
    frame = layer(n)
    ImageDraw.Draw(frame).rounded_rectangle(
        [FRAME_INSET * k, FRAME_INSET * k, (SIZE - FRAME_INSET) * k, (SIZE - FRAME_INSET) * k],
        radius=FRAME_RADIUS * k, outline=GOLD + (255,), width=int(FRAME_WIDTH * k))
    out = Image.alpha_composite(out, fade(frame, 0.50))

    brackets = layer(n)
    bd = ImageDraw.Draw(brackets)
    for arm in corner_brackets():
        bd.line([(arm[0][0] * k, arm[0][1] * k), (arm[1][0] * k, arm[1][1] * k), (arm[2][0] * k, arm[2][1] * k)],
                fill=GOLD_BRIGHT + (255,), width=int((FRAME_WIDTH + 1.5) * k), joint='curve')
    out = Image.alpha_composite(out, fade(brackets, 0.78))

    def ring_color(deg):
        return mix(EMBER, WARN, warn_weight(deg))

    def hot_color(deg):
        return mix(EMBER_HOT, mix(WARN, WHITE_HOT, 0.35), warn_weight(deg))

    # --- the FATE marker at the centre, on the ground -----------------------
    marker = layer(n)
    md = ImageDraw.Draw(marker)
    mr = 22
    md.polygon([((RCX) * k, (RCY - mr) * k), ((RCX + mr * 1.6) * k, RCY * k),
                (RCX * k, (RCY + mr) * k), ((RCX - mr * 1.6) * k, RCY * k)], fill=GOLD + (255,))
    out = Image.alpha_composite(out, fade(marker.filter(ImageFilter.GaussianBlur(10 * k)), 0.6))
    out = Image.alpha_composite(out, fade(marker, 0.45))

    # --- the far half of the barrier wall, behind everything else -----------
    def wall(back):
        w = layer(n)
        wd = ImageDraw.Draw(w)
        for j in range(WALL_LAYERS):
            t = j / (WALL_LAYERS - 1)
            draw_ring(wd, k, 3.2, lift=WALL * t, alpha=0.55 * (1 - t) ** 1.5, color_fn=ring_color, back=back)
        return w

    out = Image.alpha_composite(out, wall(True))

    base_ring = layer(n)
    draw_ring(ImageDraw.Draw(base_ring), k, 7, color_fn=ring_color, back=True)
    out = Image.alpha_composite(out, fade(base_ring.filter(ImageFilter.GaussianBlur(9 * k)), 0.9))
    out = Image.alpha_composite(out, fade(base_ring, 0.85))

    # --- near half: wall, glow, line and hot core -----------------------------
    out = Image.alpha_composite(out, wall(False))

    near = layer(n)
    draw_ring(ImageDraw.Draw(near), k, 10, color_fn=ring_color, back=False)
    out = Image.alpha_composite(out, fade(near.filter(ImageFilter.GaussianBlur(14 * k)), 1.0))
    out = Image.alpha_composite(out, near)

    core = layer(n)
    draw_ring(ImageDraw.Draw(core), k, 3.5, color_fn=hot_color, back=None)
    out = Image.alpha_composite(out, fade(core, 0.9))

    # --- the warning gem over the edge -----------------------------------------
    gx, gy = ring_point(WARN_ANGLE)
    cy = gy - GEM_LIFT

    pillar = layer(n)
    pd = ImageDraw.Draw(pillar)
    for i in range(60):
        t = i / 59
        y = gy - (GEM_LIFT - GEM_R) * t
        pd.ellipse([(gx - 3) * k, (y - 3) * k, (gx + 3) * k, (y + 3) * k], fill=WARN + (int(200 * (1 - t) + 40),))
    out = Image.alpha_composite(out, pillar)

    halo = layer(n)
    ImageDraw.Draw(halo).ellipse([(gx - GEM_R * 1.6) * k, (cy - GEM_R * 1.6) * k, (gx + GEM_R * 1.6) * k, (cy + GEM_R * 1.6) * k],
                                 fill=WARN + (255,))
    out = Image.alpha_composite(out, fade(halo.filter(ImageFilter.GaussianBlur(22 * k)), 0.55))

    gem_pts = [(gx * k, (cy - GEM_R) * k), ((gx + GEM_R * 0.8) * k, cy * k),
               (gx * k, (cy + GEM_R) * k), ((gx - GEM_R * 0.8) * k, cy * k)]
    gem_mask = Image.new('L', (n, n), 0)
    ImageDraw.Draw(gem_mask).polygon(gem_pts, fill=255)
    field = np.zeros((n, n, 3), dtype=np.float32)
    tv = np.clip((ys - (cy - GEM_R) * k) / (2 * GEM_R * k), 0, 1)
    top, bottom = mix(WARN, WHITE_HOT, 0.45), mix(WARN, (0, 0, 0), 0.25)
    for c in range(3):
        field[:, :, c] = top[c] * (1 - tv) + bottom[c] * tv
    gem = Image.fromarray(np.clip(field, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    gem.putalpha(gem_mask)
    out = Image.alpha_composite(out, gem)

    outline = layer(n)
    ImageDraw.Draw(outline).polygon(gem_pts, outline=mix(WARN, WHITE_HOT, 0.7) + (255,), width=int(3 * k))
    out = Image.alpha_composite(out, outline)

    mark = layer(n)
    mdr = ImageDraw.Draw(mark)
    bw = GEM_R * 0.14
    ink = (0x1A, 0x0C, 0x0A, 255)
    mdr.rounded_rectangle([(gx - bw) * k, (cy - GEM_R * 0.52) * k, (gx + bw) * k, (cy + GEM_R * 0.14) * k], radius=bw * k, fill=ink)
    mdr.ellipse([(gx - bw * 1.2) * k, (cy + GEM_R * 0.30) * k, (gx + bw * 1.2) * k, (cy + GEM_R * 0.30 + bw * 2.4) * k], fill=ink)
    out = Image.alpha_composite(out, mark)

    # Keep everything inside the rounded panel.
    out.putalpha(Image.fromarray(np.minimum(np.asarray(out.getchannel('A')), np.asarray(panel_mask)), 'L'))

    icon = out.resize((size, size), Image.LANCZOS)
    icon.save(path)
    return icon


if __name__ == '__main__':
    render_png('icon.png')
    print('icon.png written')
