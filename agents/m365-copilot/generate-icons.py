"""Generate Microsoft 365 Copilot agent icons from scratch using Pillow."""
import math
from PIL import Image, ImageDraw

OUT_DIR = r"D:\git\uakb\agents\m365-copilot\appPackage"

# ─── Color icon (192×192) — matches docs/images/logo.svg style ───
def make_color_icon():
    size = 192
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    draw = ImageDraw.Draw(img)

    cx, cy = size // 2, size // 2
    # Use a single accent color (gradient hard with Pillow, pick the middle)
    accent = (139, 92, 246, 255)  # purple-500
    accent_blue = (59, 130, 246, 255)
    accent_cyan = (6, 182, 212, 255)

    # Outer gear circle
    r_outer = 51
    sw = 6
    draw.ellipse((cx - r_outer, cy - r_outer, cx + r_outer, cy + r_outer),
                 outline=accent, width=sw)
    # Inner gear circle
    r_inner = 22
    draw.ellipse((cx - r_inner, cy - r_inner, cx + r_inner, cy + r_inner),
                 outline=accent, width=4)
    # Center dot
    r_dot = 8
    draw.ellipse((cx - r_dot, cy - r_dot, cx + r_dot, cy + r_dot),
                 fill=accent + (), outline=None)

    # Gear teeth (6 spokes)
    teeth_inner = 51
    teeth_outer = 67
    angles = [-90, -30, 30, 90, 150, 210]
    for ang in angles:
        rad = math.radians(ang)
        x1 = cx + math.cos(rad) * teeth_inner
        y1 = cy + math.sin(rad) * teeth_inner
        x2 = cx + math.cos(rad) * teeth_outer
        y2 = cy + math.sin(rad) * teeth_outer
        draw.line((x1, y1, x2, y2), fill=accent, width=sw)

    # Knowledge graph nodes (top right)
    nodes = [(144, 40, 8, accent_blue), (168, 80, 7, accent), (147, 115, 7, accent_cyan)]
    edges = [(144, 40, 168, 80, accent), (168, 80, 147, 115, accent_cyan),
             (144, 40, 125, 56, accent_blue)]
    # Draw edges first (semi-transparent)
    for x1, y1, x2, y2, color in edges:
        c = color[:3] + (140,)  # 55% opacity
        draw.line((x1, y1, x2, y2), fill=c, width=3)
    for x, y, r, color in nodes:
        c = color[:3] + (140,)
        draw.ellipse((x - r, y - r, x + r, y + r), fill=c)

    img.save(f"{OUT_DIR}/color.png", "PNG")
    print(f"Wrote color.png ({size}×{size})")


# ─── Outline icon (32×32) — white silhouette on transparent ───
def make_outline_icon():
    size = 32
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    draw = ImageDraw.Draw(img)

    cx, cy = size // 2, size // 2
    white = (255, 255, 255, 255)

    # Simple gear: outer ring + 6 teeth
    r_outer = 9
    sw = 2
    draw.ellipse((cx - r_outer, cy - r_outer, cx + r_outer, cy + r_outer),
                 outline=white, width=sw)
    # Center dot
    r_dot = 2
    draw.ellipse((cx - r_dot, cy - r_dot, cx + r_dot, cy + r_dot), fill=white)

    # Gear teeth
    teeth_inner = 9
    teeth_outer = 14
    angles = [-90, -30, 30, 90, 150, 210]
    for ang in angles:
        rad = math.radians(ang)
        x1 = cx + math.cos(rad) * teeth_inner
        y1 = cy + math.sin(rad) * teeth_inner
        x2 = cx + math.cos(rad) * teeth_outer
        y2 = cy + math.sin(rad) * teeth_outer
        draw.line((x1, y1, x2, y2), fill=white, width=sw)

    img.save(f"{OUT_DIR}/outline.png", "PNG")
    print(f"Wrote outline.png ({size}×{size})")


if __name__ == "__main__":
    make_color_icon()
    make_outline_icon()
