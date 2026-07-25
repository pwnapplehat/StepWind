r"""Generates StepWind's installer artwork from the app's design tokens.

The three PNGs this writes are committed next to it, so building the installer never
needs Python — this script exists so the art can be regenerated when the brand changes
instead of being an unreproducible binary blob.

Sizes come from Inno Setup 6.7's documented image areas:
  backdrop.png  WizardBackImageFile   aspect 497:360, largest area 1630x1148 (250% DPI)
  panel.png     WizardImageFile       aspect 164:314, largest area  534x1022 (250% DPI)
  logo.png      WizardSmallImageFile  square,         largest area  159x159  (250% DPI)

Run:  python installer\brand\make-art.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

# App design tokens (src/StepWind.App/web/styles.css + the site).
BG = (10, 12, 16)        # #0A0C10  app canvas
SURFACE = (13, 16, 22)   # #0D1016  raised surface
INDIGO = (99, 102, 241)  # #6366F1  brand start
CYAN = (34, 211, 238)    # #22D3EE  brand end
TEXT = (232, 235, 242)
MUTED = (139, 147, 163)

HERE = Path(__file__).resolve().parent
ICON = HERE.parent.parent / "assets" / "icon.png"
FONTS = Path("C:/Windows/Fonts")


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / name), size)


def vertical_gradient(size: tuple[int, int], top: tuple, bottom: tuple) -> Image.Image:
    """Smooth two-stop vertical gradient (built one row tall, then stretched)."""
    w, h = size
    strip = Image.new("RGB", (1, h))
    px = strip.load()
    for y in range(h):
        t = y / max(1, h - 1)
        px[0, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
    return strip.resize((w, h), Image.BICUBIC)


def horizontal_gradient(size: tuple[int, int], left: tuple, right: tuple) -> Image.Image:
    """Smooth two-stop horizontal gradient (built one column wide, then stretched)."""
    w, h = size
    strip = Image.new("RGB", (w, 1))
    px = strip.load()
    for x in range(w):
        t = x / max(1, w - 1)
        px[x, 0] = tuple(round(a + (b - a) * t) for a, b in zip(left, right))
    return strip.resize((w, h), Image.BICUBIC)


def glow(base: Image.Image, center: tuple[int, int], radius: int, color: tuple, peak: int) -> None:
    """Paint a soft radial glow, CSS `radial-gradient(closest-side, ...)` style.

    The falloff is computed once in a small square and scaled up, which is both fast and
    smoother than plotting every pixel at full size.
    """
    samples = 160
    mask = Image.new("L", (samples, samples), 0)
    px = mask.load()
    r = samples / 2
    for y in range(samples):
        dy = (y - r + 0.5) / r
        for x in range(samples):
            dx = (x - r + 0.5) / r
            d = (dx * dx + dy * dy) ** 0.5
            if d < 1.0:
                px[x, y] = round(peak * (1.0 - d) ** 2)
    # BILINEAR, not BICUBIC: cubic resampling overshoots at the mask edge and leaves a faint
    # square seam where the glow should have already faded to nothing.
    mask = mask.resize((radius * 2, radius * 2), Image.BILINEAR)
    full = Image.new("L", base.size, 0)
    full.paste(mask, (center[0] - radius, center[1] - radius))
    base.paste(Image.new("RGB", base.size, color), (0, 0), full)


def wind(size: tuple[int, int], streaks: list[tuple], scale: int = 3, blur: float = 1.6) -> Image.Image:
    """Drifting light streaks — the same motif as the app background and stepwind.app.

    Each streak is drawn as segments whose alpha rises and falls, so the line tapers at
    both ends instead of stopping dead. Drawn oversized and downsampled for clean edges.
    """
    w, h = size
    layer = Image.new("RGBA", (w * scale, h * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    for x, y, length, thickness, alpha, color in streaks:
        segments = 48
        for i in range(segments):
            t0, t1 = i / segments, (i + 1) / segments
            # sine profile: transparent at both ends, brightest in the middle
            fade = (max(0.0, min(1.0, (t0 + t1) / 2)) * 3.14159) ** 0
            profile = (1 - abs(((t0 + t1) / 2) * 2 - 1)) ** 1.5
            a = round(alpha * profile * fade)
            if a <= 0:
                continue
            x0 = (x + length * t0) * scale
            x1 = (x + length * t1) * scale
            y0 = (y - length * 0.13 * t0) * scale
            y1 = (y - length * 0.13 * t1) * scale
            draw.line([(x0, y0), (x1, y1)], fill=color + (a,), width=max(1, round(thickness * scale)))
    layer = layer.resize((w, h), Image.LANCZOS)
    return layer.filter(ImageFilter.GaussianBlur(blur))


def dot_grid(size: tuple[int, int], spacing: int, radius: int, alpha: int) -> Image.Image:
    """Faint dot texture with a diagonal fade — the app's timeline grid."""
    w, h = size
    scale = 2
    dots = Image.new("RGBA", (w * scale, h * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(dots)
    for y in range(0, h * scale, spacing * scale):
        for x in range(0, w * scale, spacing * scale):
            draw.ellipse(
                [x, y, x + radius * scale, y + radius * scale],
                fill=(255, 255, 255, alpha),
            )
    dots = dots.resize((w, h), Image.LANCZOS)

    fade = Image.new("L", (w, h))
    px = fade.load()
    for y in range(h):
        for x in range(w):
            t = (x / w) * 0.6 + (y / h) * 0.4        # brightest top-left, gone bottom-right
            px[x, y] = round(255 * max(0.0, 1.0 - t * 1.35))
    dots.putalpha(Image.composite(dots.getchannel("A"), Image.new("L", (w, h), 0), fade))
    return dots


def build_backdrop() -> Image.Image:
    """Page background for every wizard page. Deliberately quiet: wizard text sits on it."""
    size = (1656, 1200)
    img = vertical_gradient(size, BG, (12, 14, 19))
    # Only two glows, both anchored off-centre. A third "lift" behind the text column read
    # as a visible disc against the near-black canvas, so the text column stays flat.
    #
    # Both sit right of centre on purpose: on the welcome and finished pages the left third of
    # this image is covered by panel.png, so glows placed there are simply never seen and the
    # content column reads as flat black.
    glow(img, (760, 90), 820, INDIGO, 44)
    glow(img, (1560, 1160), 680, CYAN, 30)
    img = Image.alpha_composite(img.convert("RGBA"), dot_grid(size, 26, 2, 22))
    img = Image.alpha_composite(img, wind(size, [
        (-120, 300, 620, 2, 46, INDIGO),
        (120, 520, 900, 2, 34, (255, 255, 255)),
        (620, 190, 700, 2, 30, CYAN),
        (980, 880, 780, 2, 34, INDIGO),
        (240, 1010, 560, 2, 26, (255, 255, 255)),
        (1180, 430, 520, 2, 26, CYAN),
    ]))
    return img.convert("RGB")


def build_panel() -> Image.Image:
    """Tall left column shown on the Welcome and Finished pages: the brand moment."""
    size = (548, 1050)
    img = vertical_gradient(size, (12, 15, 21), (9, 11, 15))
    glow(img, (150, 120), 460, INDIGO, 92)
    glow(img, (470, 980), 430, CYAN, 68)
    img = img.convert("RGBA")
    img = Image.alpha_composite(img, wind(size, [
        (-80, 620, 460, 2, 52, INDIGO),
        (60, 780, 520, 2, 38, (255, 255, 255)),
        (180, 500, 420, 2, 30, CYAN),
    ], blur=1.2))

    icon = Image.open(ICON).convert("RGBA").resize((156, 156), Image.LANCZOS)
    img.alpha_composite(icon, (74, 168))

    draw = ImageDraw.Draw(img)
    draw.text((74, 372), "StepWind", font=font("seguisb.ttf", 66), fill=TEXT)

    # brand rule: indigo -> cyan, matching the app's accent sweep
    rule = Image.new("RGB", (168, 4))
    rpx = rule.load()
    for x in range(168):
        t = x / 167
        rpx[x, 0] = tuple(round(a + (b - a) * t) for a, b in zip(INDIGO, CYAN))
    for y in range(1, 4):
        for x in range(168):
            rpx[x, y] = rpx[x, 0]
    img.alpha_composite(rule.convert("RGBA"), (76, 470))

    draw.text((74, 962), "stepwind.app", font=font("seguisb.ttf", 34), fill=CYAN)
    return img.convert("RGB")


def build_logo() -> Image.Image:
    """Square mark for the upper-right corner of the inner pages (alpha preserved)."""
    return Image.open(ICON).convert("RGBA").resize((256, 256), Image.LANCZOS)


# The wizard's progress bar is a native TNewProgressBar, which exposes no colour property and is
# painted lime green by the style. These two strips are stretched over it instead (see the
# progress-bar block in stepwind.iss) — pixels are the one thing a VCL style cannot restyle.
# Oversized on purpose so they stay sharp when stretched at 200%+ DPI.
def build_bar_fill() -> Image.Image:
    return horizontal_gradient((1200, 24), INDIGO, CYAN)


def build_bar_track() -> Image.Image:
    return Image.new("RGB", (1200, 24), (23, 27, 36))


def main() -> None:
    for name, image in (
        ("backdrop.png", build_backdrop()),
        ("panel.png", build_panel()),
        ("logo.png", build_logo()),
        ("bar-fill.png", build_bar_fill()),
        ("bar-track.png", build_bar_track()),
    ):
        out = HERE / name
        image.save(out, "PNG", optimize=True)
        print(f"{name:14} {image.size[0]}x{image.size[1]}  {out.stat().st_size / 1024:7.1f} KB")


if __name__ == "__main__":
    main()
