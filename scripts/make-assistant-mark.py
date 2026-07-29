"""Draws assistant_mark.png, the small dino mark used next to Lucide glyphs in the UI.

The app icon's dino is soft-shaded line art: downscaled to 16-24px its outlines, teeth and eye
collapse into a green smudge, so this mark is drawn as flat geometry instead of resampled from
AppIcon.icon/Assets/dino.png. Two filled shapes (head, jaw) plus a white eye, sized so no feature
is thinner than a pixel at 16px.

Also writes scripts/out/mark-preview.png: the mark at 16/18/20/24px on light and dark, beside a
Lucide glyph at the same size, with an 8x nearest-neighbour blow-up of each.

Usage: python scripts/make-assistant-mark.py [output_dir]

Requires Pillow.
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
ASSETS = os.path.join(ROOT, 'GitBench', 'Assets')
LUCIDE = os.path.join(ASSETS, 'Fonts', 'Lucide', 'Lucide.ttf')
PREVIEW = os.path.join(ROOT, 'scripts', 'out', 'mark-preview.png')

# The dino's body green, sampled from the source art. Reads against both themes' surfaces, so the
# mark ships coloured rather than as a tintable white silhouette.
GREEN = (57, 165, 15, 255)
EYE = (255, 255, 255, 255)

# 4x the largest intended use (24px); the GUI downscales at draw time, as it does for app_icon.png.
SIZE = 96
SUPERSAMPLE = 4

# Corner rounding, in units of the 100x100 design box. Applied by blurring the silhouette mask and
# re-thresholding, which softens every joint at once. Larger than ~2 starts eating the nose.
ROUND = 1.6

# Head in profile facing right, on a 100x100 design box: skull, brow ledge, snout, closed jaw, then
# down the throat into a neck stub so the mark doesn't read as a severed head. One closed outline —
# a separate lower jaw shape leaves a gap that reads as a detached sliver below 20px.
HEAD = [
    (10, 40), (12, 26), (20, 15), (34, 8), (48, 7), (58, 12), (61, 23),
    (74, 18), (88, 20), (96, 29), (97, 45), (92, 58), (80, 66), (58, 72), (40, 74),
    (30, 70), (33, 84), (38, 97), (4, 97), (1, 68), (6, 52),
]

# Neck and chest, widening the throat so it survives as a solid mass rather than a hairline.
NECK = [
    (8, 46), (32, 62), (36, 84), (40, 97), (4, 97), (1, 66),
]

# The mouth, punched out of the finished silhouette. Tapers from 9 units at the snout to 6 at the
# corner: below 20px it closes into a seam and the head reads as one mass, which is the right trade
# at that size.
MOUTH = [(94, 45), (94, 56), (58, 61), (43, 55)]

EYE_CENTER = (33, 28)
EYE_RADIUS = 8.0

# Nostril, only resolvable from ~32px up; harmless below that.
NOSTRIL_CENTER = (89, 33)
NOSTRIL_RADIUS = 2.5

PREVIEW_SIZES = [16, 18, 20, 24]
PREVIEW_ZOOM = 8
LIGHT_BG = (250, 250, 250)
DARK_BG = (24, 24, 27)


def scaled(points, side):
    return [(x / 100.0 * side, y / 100.0 * side) for x, y in points]


def silhouette(side):
    """The union of the head, jaw and neck shapes as a rounded alpha mask."""
    mask = Image.new('L', (side, side), 0)
    draw = ImageDraw.Draw(mask)
    for shape in (HEAD, NECK):
        draw.polygon(scaled(shape, side), fill=255)
    radius = ROUND / 100.0 * side
    mask = mask.filter(ImageFilter.GaussianBlur(radius))
    return mask.point(lambda v: 255 if v >= 128 else 0)


def ellipse(draw, center, radius, side, fill):
    cx = center[0] / 100.0 * side
    cy = center[1] / 100.0 * side
    r = radius / 100.0 * side
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=fill)


def render(size):
    hi = size * SUPERSAMPLE
    mark = Image.new('RGBA', (hi, hi), (GREEN[0], GREEN[1], GREEN[2], 0))
    mark.putalpha(silhouette(hi))
    mark = Image.composite(Image.new('RGBA', (hi, hi), GREEN), mark, mark.getchannel('A'))

    draw = ImageDraw.Draw(mark)
    draw.polygon(scaled(MOUTH, hi), fill=(0, 0, 0, 0))
    ellipse(draw, EYE_CENTER, EYE_RADIUS, hi, EYE)
    ellipse(draw, NOSTRIL_CENTER, NOSTRIL_RADIUS, hi, (0, 0, 0, 0))
    return mark.resize((size, size), Image.LANCZOS)


def glyph_tile(char, size, color):
    """A Lucide glyph rendered to a transparent tile of the same box as the mark."""
    tile = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    font = ImageFont.truetype(LUCIDE, size)
    ImageDraw.Draw(tile).text((size / 2, size / 2), char, font=font, fill=color, anchor='mm')
    return tile


def lucide_char():
    """SquareTerminal's codepoint, read out of LucideIcons.cs so the two can't drift."""
    path = os.path.join(ROOT, 'GitBench', 'Controls', 'LucideIcons.cs')
    with open(path, encoding='utf-8') as f:
        for line in f:
            if 'SquareTerminal' in line:
                return line.split('"')[1]
    raise SystemExit('SquareTerminal not found in LucideIcons.cs')


def preview_panel(mark, char, bg, fg):
    pad = 14
    gap = 18
    label_h = 16
    cols = []
    for size in PREVIEW_SIZES:
        small = mark.resize((size, size), Image.LANCZOS)
        icon = glyph_tile(char, size, fg)
        strip = Image.new('RGBA', (size * 2 + gap, size), (0, 0, 0, 0))
        strip.alpha_composite(small, (0, 0))
        strip.alpha_composite(icon, (size + gap, 0))
        zoom = strip.resize((strip.width * PREVIEW_ZOOM, strip.height * PREVIEW_ZOOM), Image.NEAREST)
        cols.append((size, strip, zoom))

    width = pad * 2 + sum(c[2].width for c in cols) + gap * 3 * (len(cols) - 1)
    height = pad * 2 + label_h + max(c[2].height for c in cols) + gap + max(c[1].height for c in cols)
    panel = Image.new('RGBA', (width, height), bg + (255,))
    draw = ImageDraw.Draw(panel)

    x = pad
    for size, strip, zoom in cols:
        draw.text((x, pad), f'{size}px', fill=fg)
        panel.alpha_composite(strip, (x, pad + label_h))
        panel.alpha_composite(zoom, (x, pad + label_h + strip.height + gap))
        x += zoom.width + gap * 3
    return panel


def write_preview(mark):
    char = lucide_char()
    light = preview_panel(mark, char, LIGHT_BG, (24, 24, 27, 255))
    dark = preview_panel(mark, char, DARK_BG, (228, 228, 231, 255))
    out = Image.new('RGBA', (max(light.width, dark.width), light.height + dark.height))
    out.alpha_composite(light, (0, 0))
    out.alpha_composite(dark, (0, light.height))
    os.makedirs(os.path.dirname(PREVIEW), exist_ok=True)
    out.save(PREVIEW)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else ASSETS
    os.makedirs(out, exist_ok=True)

    mark = render(SIZE)
    mark.save(os.path.join(out, 'assistant_mark.png'))
    write_preview(mark)
    print(f'wrote assistant_mark.png ({SIZE}px) -> {out}')
    print(f'wrote {PREVIEW}')


if __name__ == '__main__':
    main()
