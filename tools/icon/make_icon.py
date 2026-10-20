"""Generates the DackelFXP program icon: a dachshund running on a transfer arrow.

    python tools/icon/make_icon.py

Writes src/DackelFxp.App/Assets/dackelfxp.ico (16-256 px) and dackelfxp.png (256 px, Linux window icon),
plus tools/icon/dackelfxp-1024.png as a preview. Drawn at 1024 px and downscaled for smooth edges;
sizes up to 32 px use a simplified drawing (no speed lines, collar, mouth) so they stay readable.
Requires Pillow.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

S = 1024
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "src" / "DackelFxp.App" / "Assets"

BG_TOP = (30, 58, 138)       # indigo
BG_BOTTOM = (14, 165, 233)   # sky blue
FUR = (210, 130, 62)         # caramel
FUR_DARK = (128, 66, 26)     # ears, legs
BELLY = (188, 110, 48)
ARROW = (255, 205, 50)       # warm yellow
NOSE = (35, 25, 20)


def gradient(size, top, bottom):
    img = Image.new("RGB", (size, size))
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / (size - 1)
        d.line([(0, y), (size, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
    return img


def layer():
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def pill(d, box, fill):
    """Rounded bar; radius stays below half the short side (Pillow draws artifacts otherwise)."""
    x0, y0, x1, y1 = box
    r = int(min(x1 - x0, y1 - y0) / 2) - 1
    d.rounded_rectangle(box, r, fill=fill)


def draw_icon(simple=False):
    img = layer()
    margin, radius = 40, 210

    # Tile with soft drop shadow.
    shadow = layer()
    ImageDraw.Draw(shadow).rounded_rectangle((margin + 6, margin + 22, S - margin + 6, S - margin + 22), radius, fill=(0, 0, 0, 120))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(16)))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, S - margin, S - margin), radius, fill=255)
    tile = gradient(S, BG_TOP, BG_BOTTOM).convert("RGBA")
    tile.putalpha(mask)
    img.alpha_composite(tile)

    # Transfer arrow (the track the dachshund runs on).
    track = layer()
    d = ImageDraw.Draw(track)
    y = 812
    pill(d, (110, y - 42, 760, y + 42), ARROW)
    d.polygon([(730, y - 125), (915, y), (730, y + 125)], fill=ARROW)
    img.alpha_composite(track)

    if not simple:
        # Speed lines behind the dog (translucent white).
        lines = layer()
        d = ImageDraw.Draw(lines)
        for x0, yy, w in [(90, 455, 120), (70, 545, 150), (100, 635, 110)]:
            pill(d, (x0, yy - 16, x0 + w, yy + 16), (255, 255, 255, 140))
        img.alpha_composite(lines)

    dog = layer()
    d = ImageDraw.Draw(dog)
    # Tail.
    d.line([(255, 500), (205, 395), (185, 325)], fill=FUR, width=54, joint="curve")
    d.ellipse((158, 298, 212, 352), fill=FUR)
    # Short legs.
    for x in (255, 345, 595, 685):
        d.rectangle((x, 610, x + 70, 717), fill=FUR_DARK)
        d.ellipse((x, 682, x + 70, 752), fill=FUR_DARK)
    # Long body.
    d.rounded_rectangle((215, 430, 790, 680), 120, fill=FUR)
    pill(d, (300, 615, 720, 672), BELLY)
    # Neck and head.
    d.polygon([(660, 450), (735, 345), (845, 380), (800, 560)], fill=FUR)
    d.ellipse((690, 255, 905, 470), fill=FUR)
    # Snout and nose.
    pill(d, (820, 340, 958, 440), FUR)
    d.ellipse((918, 352, 972, 406), fill=NOSE)
    if not simple:
        d.line([(870, 428), (935, 428)], fill=FUR_DARK, width=10)
    img.alpha_composite(dog)

    # Floppy ear (rotated ellipse).
    ear = layer()
    ImageDraw.Draw(ear).ellipse((690, 285, 800, 520), fill=FUR_DARK)
    img.alpha_composite(ear.rotate(-20, center=(750, 295), resample=Image.BICUBIC))

    d = ImageDraw.Draw(img)
    # Eye (bigger in the simple version so it survives 16 px).
    if simple:
        d.ellipse((815, 305, 875, 365), fill=(255, 255, 255))
        d.ellipse((835, 318, 870, 355), fill=NOSE)
    else:
        d.ellipse((822, 310, 870, 358), fill=(255, 255, 255))
        d.ellipse((839, 320, 866, 349), fill=NOSE)
        d.ellipse((849, 324, 858, 333), fill=(255, 255, 255))
        # Collar with tag.
        d.line([(712, 452), (782, 540)], fill=(220, 38, 38), width=28)
        d.ellipse((752, 530, 798, 576), fill=ARROW)
    return img


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    full = draw_icon()
    small = draw_icon(simple=True)
    frames = {s: (small if s <= 32 else full).resize((s, s), Image.LANCZOS) for s in [16, 20, 24, 32, 40, 48, 64, 128, 256]}
    frames[256].save(OUT / "dackelfxp.ico", sizes=[(s, s) for s in frames], append_images=[frames[s] for s in frames if s != 256])
    frames[256].save(OUT / "dackelfxp.png")
    full.save(ROOT / "tools" / "icon" / "dackelfxp-1024.png")
    small.resize((256, 256), Image.LANCZOS).save(ROOT / "tools" / "icon" / "dackelfxp-small-preview.png")
    print("written:", OUT / "dackelfxp.ico", OUT / "dackelfxp.png")


if __name__ == "__main__":
    main()
