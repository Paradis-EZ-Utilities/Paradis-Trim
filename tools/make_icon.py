"""Draws the Paradis Trim icon and saves it as .ico/.png.

It follows the Paradis EZ Utilities app-icon style set by Paradis Capture: a charcoal rounded
square with a white "P" whose bowl is the app's own symbol. Capture's bowl is a red record dot;
Trim's is a blue disc holding two white trim handles around the kept part.

Usage: python3 tools/make_icon.py src/ParadisTrim/Assets/ParadisTrim
"""
import sys
from PIL import Image, ImageDraw


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def draw(size):
    s = size * 4  # draw large, then shrink for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Charcoal rounded square with a soft top-to-bottom gradient.
    gradient = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(gradient)
    top, bottom = (62, 64, 70, 255), (32, 33, 37, 255)
    for y in range(s):
        gd.line([(0, y), (s, y)], fill=lerp(top, bottom, y / s))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([s * 0.03, s * 0.03, s * 0.97, s * 0.97], radius=s * 0.21, fill=255)
    img.paste(gradient, (0, 0), mask)

    d = ImageDraw.Draw(img)
    # The P's stem.
    d.rounded_rectangle([s * 0.24, s * 0.22, s * 0.38, s * 0.80], radius=s * 0.07, fill=(242, 243, 245, 255))
    # The bowl: dark ring, then the blue disc.
    cx, cy, r = s * 0.555, s * 0.395, s * 0.215
    ring = s * 0.035
    d.ellipse([cx - r - ring, cy - r - ring, cx + r + ring, cy + r + ring], fill=(36, 37, 41, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(76, 141, 255, 255))
    # The kept part between two trim handles.
    d.rounded_rectangle([cx - r * 0.42, cy - r * 0.30, cx + r * 0.42, cy + r * 0.30], radius=s * 0.012,
                        fill=(186, 212, 255, 255))
    # A little waveform inside the kept part, so it reads as sound/video being trimmed.
    for i, h in enumerate((0.12, 0.22, 0.15)):
        bx = cx + r * (-0.22 + i * 0.22)
        d.rounded_rectangle([bx - r * 0.045, cy - r * h, bx + r * 0.045, cy + r * h], radius=s * 0.006,
                            fill=(47, 111, 228, 255))
    for x in (-0.52, 0.52):
        hx = cx + r * x
        d.rounded_rectangle([hx - r * 0.10, cy - r * 0.58, hx + r * 0.10, cy + r * 0.58], radius=s * 0.012,
                            fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


out = sys.argv[1]
big = draw(256)
big.save(out + ".png")
sizes = (16, 20, 24, 32, 40, 48, 64, 128)
big.save(out + ".ico", sizes=[(n, n) for n in sizes] + [(256, 256)], append_images=[draw(n) for n in sizes])
