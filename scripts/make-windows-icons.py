"""Regenerates the Windows/Linux icon assets from the Icon Composer source layer.

Outputs app_icon.ico (multi-size), app_icon.rgba (the GLFW window-icon set) and app_icon.png
(in-app logo + Linux icon) from AppIcon.icon/Assets/dino.png. macOS assets are not touched:
Icon Composer owns those and applies its own mask.

Usage: python scripts/make-windows-icons.py [output_dir]

Requires Pillow.
"""
import os
import struct
import sys

from PIL import Image, ImageFilter

ASSETS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'GitBench', 'Assets')
SOURCE = os.path.join(ASSETS, 'AppIcon.icon', 'Assets', 'dino.png')

# Matches the "fill" solid in AppIcon.icon/icon.json so every platform lands on the same blue.
FILL = (34, 62, 181, 255)

# Superellipse exponent for the tile mask. 5 approximates the continuous-curvature squircle
# that iOS/macOS use, which reads rounder than a plain rounded rectangle at the same radius.
SQUIRCLE_N = 5.0

# Regions of the 1024x1024 source. Up to 64px the legs, tail and facial detail collapse into
# noise, so those entries zoom in: HEAD keeps the head and chest, FACE goes tighter still.
# Both stop short of x=735, where the tail tip sits detached from the body and would otherwise
# survive the crop as a stray sliver against the fill.
BODY = (210, 111, 844, 921)
HEAD = (150, 60, 725, 635)
FACE = (230, 90, 700, 560)

# Downscaling from 1024 softens the eye and teeth past the point where they read. A light
# unsharp pass restores them; anything stronger rings the eye with a dark halo.
SHARPEN = (0.6, 60, 3)

# size -> (crop, fraction of the tile the subject spans, unsharp settings or None)
# HEAD holds the same framing from 32 through 64 so the taskbar looks the same whatever entry the
# shell lands on: it asks for 32/40/48 at 100/125/150% scaling, and rounds up to the next entry
# when there is no exact match.
LADDER = {
    16: (FACE, 1.00, SHARPEN),
    32: (HEAD, 1.00, SHARPEN),
    40: (HEAD, 1.00, SHARPEN),
    48: (HEAD, 1.00, SHARPEN),
    64: (HEAD, 1.00, SHARPEN),
    128: (BODY, 0.88, None),
    256: (BODY, 0.88, None),
    1024: (BODY, 0.88, None),
}

ICON_SIZES = [16, 32, 40, 48, 64, 128, 256]

# Anything larger is emitted as PNG inside the .ico; smaller entries stay uncompressed BMP,
# which every shell surface can decode.
ICO_PNG_THRESHOLD = 128

SUPERSAMPLE = 4


def squircle_mask(size):
    """An antialiased superellipse alpha mask, rendered oversized and boxed down."""
    hi = size * SUPERSAMPLE
    half = hi / 2.0
    rows = bytearray()
    for y in range(hi):
        v = abs((y + 0.5 - half) / half) ** SQUIRCLE_N
        if v > 1.0:
            rows += bytes(hi)
            continue
        limit = (1.0 - v) ** (1.0 / SQUIRCLE_N)
        x0 = max(0, int(half - limit * half))
        x1 = min(hi, int(half + limit * half))
        rows += bytes(x0) + b'\xff' * (x1 - x0) + bytes(hi - x1)
    return Image.frombytes('L', (hi, hi), bytes(rows)).resize((size, size), Image.BOX)


def render(dino, size):
    """Composes the framed subject over the fill and clips it to the squircle."""
    crop, fraction, sharpen = LADDER[size]
    sub = dino.crop(crop)
    scale = (size * fraction) / max(sub.width, sub.height)
    w = max(1, round(sub.width * scale))
    h = max(1, round(sub.height * scale))
    sub = sub.resize((w, h), Image.LANCZOS)

    tile = Image.new('RGBA', (size, size), FILL)
    tile.alpha_composite(sub, ((size - w) // 2, (size - h) // 2))
    if sharpen:
        tile = tile.filter(ImageFilter.UnsharpMask(*sharpen))
    tile.putalpha(squircle_mask(size))
    return tile


def bmp_entry(img):
    """A 32bpp bottom-up DIB plus its 1bpp AND mask, as an .ico stores sub-256 entries."""
    w, h = img.size
    pixels = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = pixels[x, y]
            xor += bytes((b, g, r, a))

    stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if pixels[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row

    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return bytes(header + xor + mask)


def png_entry(img):
    import io
    buf = io.BytesIO()
    img.save(buf, format='PNG', optimize=True)
    return buf.getvalue()


def write_ico(path, tiles):
    entries = []
    for size, img in tiles:
        blob = png_entry(img) if size >= ICO_PNG_THRESHOLD else bmp_entry(img)
        entries.append((size, blob))

    offset = 6 + 16 * len(entries)
    directory = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
    for size, blob in entries:
        dim = 0 if size >= 256 else size
        directory += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    with open(path, 'wb') as f:
        f.write(directory)
        for _, blob in entries:
            f.write(blob)


def write_rgba(path, tiles):
    """The window-icon set GuiApp.SetIcon reads: count, then (width, height, RGBA) per image."""
    with open(path, 'wb') as f:
        f.write(struct.pack('<i', len(tiles)))
        for size, img in tiles:
            f.write(struct.pack('<ii', size, size))
            f.write(img.tobytes('raw', 'RGBA'))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else ASSETS
    os.makedirs(out, exist_ok=True)

    dino = Image.open(SOURCE).convert('RGBA')
    tiles = [(size, render(dino, size)) for size in ICON_SIZES]

    write_ico(os.path.join(out, 'app_icon.ico'), tiles)
    write_rgba(os.path.join(out, 'app_icon.rgba'), tiles)
    render(dino, 1024).save(os.path.join(out, 'app_icon.png'))

    names = {id(BODY): 'body', id(HEAD): 'head', id(FACE): 'face'}
    for size, _ in tiles:
        crop, fraction, sharpen = LADDER[size]
        print(f'{size:>4}px  crop={names[id(crop)]}  fill={fraction}  sharpen={bool(sharpen)}')
    print(f'wrote app_icon.ico, app_icon.rgba, app_icon.png -> {out}')


if __name__ == '__main__':
    main()
