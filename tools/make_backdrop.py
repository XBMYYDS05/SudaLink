"""生成 SudaLink 的窗口底衬（light / dark）。

玻璃能不能被"看见"，取决于背后的底衬有没有结构：一片纯渐变模糊前后没有差别，
所以这里做的是有边界的色团 + 斜向光带 + 细颗粒，让模糊和边缘折射都能读出层次。
"""

import math
import random
from PIL import Image, ImageDraw, ImageFilter

W, H = 900, 1300
random.seed(7)


def vertical(w, h, top, bottom):
    img = Image.new("RGB", (1, h))
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        px[0, y] = (
            round(top[0] + (bottom[0] - top[0]) * t),
            round(top[1] + (bottom[1] - top[1]) * t),
            round(top[2] + (bottom[2] - top[2]) * t),
        )
    return img.resize((w, h), Image.BILINEAR)


def blob(size, color, alpha, center, radius, softness=0.55):
    """一个边缘柔和但仍有边界的色团。"""
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = center
    steps = 26
    for i in range(steps, 0, -1):
        t = i / steps
        r = radius * t
        a = int(alpha * (1 - t) ** 1.6 * (1 - softness * 0.2))
        d.ellipse([cx - r, cy - r * 0.82, cx + r, cy + r * 0.82], fill=color + (a,))
    return layer.filter(ImageFilter.GaussianBlur(radius * 0.06))


def streaks(size, color, alpha):
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for i in range(3):
        offset = -260 + i * 300
        width = 130 - i * 24
        d.line([(-100, 240 + offset), (size[0] + 100, -160 + offset)],
               fill=color + (int(alpha * (0.9 - i * 0.22)),), width=width)
    return layer.filter(ImageFilter.GaussianBlur(46))


def grain(img, strength=4):
    w, h = img.size
    noise = Image.new("L", (w, h))
    px = noise.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = 128 + random.randint(-strength, strength) * 6
    noise = noise.filter(ImageFilter.GaussianBlur(0.4))
    return Image.blend(img, Image.merge("RGB", (noise, noise, noise)), 0.045)


def vignette(img, strength=0.16):
    w, h = img.size
    mask = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(mask)
    d.ellipse([-w * 0.35, -h * 0.28, w * 1.35, h * 1.28], fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(180))
    dark = Image.new("RGB", (w, h), (0, 0, 0))
    return Image.composite(img, Image.blend(img, dark, strength), mask)


def build(top, bottom, blobs, light_alpha, out):
    base = vertical(W, H, top, bottom).convert("RGBA")
    for color, alpha, center, radius in blobs:
        base = Image.alpha_composite(base, blob((W, H), color, alpha, center, radius))
    base = Image.alpha_composite(base, streaks((W, H), (255, 255, 255), light_alpha))
    img = base.convert("RGB")
    img = grain(img, 4 if out.endswith("light.jpg") else 5)
    img = vignette(img, 0.12 if out.endswith("light.jpg") else 0.3)
    img.save(out, quality=94, subsampling=1)
    print("wrote", out)


build(
    (246, 249, 255), (186, 202, 236),
    [
        ((70, 132, 255), 250, (640, 235), 470),   # 蓝
        ((255, 104, 152), 235, (95, 500), 420),   # 玫瑰
        ((60, 214, 180), 225, (790, 890), 445),   # 薄荷
        ((140, 104, 255), 235, (180, 1100), 440),   # 紫
        ((255, 186, 92), 200, (470, 690), 330),    # 暖沙
    ],
    26,
    r"D:\SUDANET\src\SudaLink\Assets\backdrop-light.jpg",
)

build(
    (12, 16, 26), (5, 7, 12),
    [
        ((46, 122, 236), 235, (640, 205), 480),
        ((168, 62, 208), 215, (130, 460), 435),
        ((16, 170, 158), 215, (790, 870), 445),
        ((74, 76, 216), 205, (220, 1110), 435),
        ((196, 78, 104), 150, (480, 650), 320),
    ],
    16,
    r"D:\SUDANET\src\SudaLink\Assets\backdrop-dark.jpg",
)
