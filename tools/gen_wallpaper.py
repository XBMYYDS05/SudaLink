"""生成 SudaLink 的玻璃底衬壁纸（浅色 / 深色）。

这版的核心目标：**让模糊有东西可模糊**。

之前的版本整屏都是高明度马卡龙色，模糊前后几乎没差别，玻璃看起来就是
一层奶白塑料。现在的做法：

1. **拉开明度**：浅色版顶部保留近白高光，底部压到中明度的饱和蓝紫，
   于是玻璃内外有明显色差、亮度差 —— 这才"透"。
2. **提高彩度**：色相仍然集中在相邻的蓝→紫→玫区间（互补色模糊后必成灰），
   但彩度提上来，平均之后还是"有颜色的紫"，而不是脏灰。
3. **细节用无色白丝**：白丝带在玻璃外边缘清晰、在玻璃内糊掉，
   这是"这块是玻璃"最直接的证据。
4. **底部压深**：让上浮的玻璃卡片有承托，投影才不显得脏。

用法：python gen_wallpaper.py [输出目录]
"""
import sys

import numpy as np
from PIL import Image, ImageFilter

W, H = 900, 1300


def hx(value):
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) / 255.0 for i in (0, 2, 4)], dtype=np.float32)


def ramp(stops, yn):
    """按纵向位置插值出一条色相坡道。"""
    out = np.zeros((H, W, 3), dtype=np.float32)
    for index in range(len(stops) - 1):
        p0, c0 = stops[index]
        p1, c1 = stops[index + 1]
        span = max(p1 - p0, 1e-6)
        t = np.clip((yn - p0) / span, 0.0, 1.0)
        t = t * t * (3 - 2 * t)
        band = c0 * (1 - t[..., None]) + c1 * t[..., None]
        mask = ((yn >= p0) & (yn <= p1))[..., None]
        out = np.where(mask, band, out)
    return out


def saturate(img, amount):
    luma = img @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    return np.clip(luma[..., None] + (img - luma[..., None]) * amount, 0.0, 1.0)


def build(dark: bool) -> Image.Image:
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn = xx / (W - 1)
    yn = yy / (H - 1)

    if dark:
        stops = [
            (0.00, hx("#070C22")),
            (0.12, hx("#0F1B52")),
            (0.28, hx("#152664")),
            (0.44, hx("#1B2768")),
            (0.60, hx("#232A6E")),
            (0.74, hx("#2C2663")),
            (0.88, hx("#1D1846")),
            (1.00, hx("#080A1C")),
        ]
        sat, ribbon, grain, vig, sheen = 1.12, 0.34, 0.013, 0.20, 0.18
        bloom = (0.30, 0.04, 0.34, 0.30, 0.26)
    else:
        stops = [
            (0.00, hx("#F4F9FF")),
            (0.10, hx("#D6E8FD")),
            (0.26, hx("#A6CCF8")),
            (0.44, hx("#7FB6F1")),
            (0.60, hx("#6BA6E9")),
            (0.74, hx("#6F93D4")),
            (0.87, hx("#7286B8")),
            (1.00, hx("#5A6A96")),
        ]
        sat, ribbon, grain, vig, sheen = 1.0, 0.92, 0.012, 0.10, 0.34
        bloom = (0.20, 0.02, 0.30, 0.24, 0.34)

    img = saturate(ramp(stops, yn), sat)

    # 左上角自然光：只提明度，不改色相
    bx, by, sw, sh, amt = bloom
    glow = np.exp(-(((xn - bx) ** 2) / (2 * sw ** 2) + ((yn - by) ** 2) / (2 * sh ** 2)))
    img = img + (1.0 - img) * np.clip(glow, 0, 1)[..., None] * amt

    # 右上角一团冷光，和左上形成对角，玻璃上会出现"两块亮区"
    glow2 = np.exp(-(((xn - 0.93) ** 2) / (2 * 0.26 ** 2) + ((yn - 0.30) ** 2) / (2 * 0.20 ** 2)))
    img = img + (1.0 - img) * np.clip(glow2, 0, 1)[..., None] * (amt * 0.55)

    # 白色丝带：无彩细节，模糊后只变软、不串色。
    # 关键是**边缘要锐**：玻璃外能看清丝带的边，玻璃里只剩一团柔光 ——
    # 这是"这块是玻璃"最直接的证据。所以这里带宽很窄、不做大范围柔化。
    for index, (offset, width, strength, tilt, amp) in enumerate(
            [(0.11, 0.0075, 1.00, 0.34, 0.050), (0.20, 0.0035, 0.55, 0.22, 0.026),
             (0.33, 0.0055, 0.86, -0.22, 0.044), (0.48, 0.0105, 0.70, 0.17, 0.030),
             (0.62, 0.0040, 0.52, -0.30, 0.052), (0.78, 0.0065, 0.60, 0.24, 0.038),
             (0.91, 0.0090, 0.42, -0.16, 0.022)]):
        curve = yn - (offset + tilt * (xn - 0.5) + amp * np.sin(xn * 5.6 + index * 1.7))
        ridge = np.exp(-(curve ** 2) / (2 * width ** 2))
        img = img + (1.0 - img) * (ridge * ribbon * strength)[..., None]

    # 窄高光：斜着擦过，玻璃上最像反光的一条
    diag = xn * 0.52 + yn * 0.86
    streak = np.exp(-((diag - 0.150) ** 2) / (2 * 0.016 ** 2))
    streak = streak + 0.55 * np.exp(-((diag - 0.295) ** 2) / (2 * 0.006 ** 2))
    streak = streak + 0.30 * np.exp(-((diag - 0.430) ** 2) / (2 * 0.0035 ** 2))
    img = img + (1.0 - img) * np.clip(streak, 0, 1)[..., None] * sheen

    # 边缘压暗，让上浮的玻璃有承托
    vig_map = 1.0 - vig * (((xn - 0.5) ** 2 + (yn - 0.5) ** 2) / 0.5) ** 1.2
    img = img * vig_map[..., None]

    # 底部再压一档：卡片浮起来需要"地面"
    img = img * (1.0 - (0.10 if not dark else 0.16) * np.clip((yn - 0.62) / 0.38, 0, 1)[..., None] ** 1.6)

    img = np.clip(img, 0.0, 1.0)
    out = Image.fromarray((img * 255).astype(np.uint8), "RGB")

    noise = np.random.default_rng(20261025).normal(0, grain * 255, (H, W, 1))
    arr = np.clip(np.asarray(out, dtype=np.float32) + noise, 0, 255).astype(np.uint8)
    return Image.fromarray(arr, "RGB").filter(ImageFilter.GaussianBlur(0.15))


if __name__ == "__main__":
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "src/SudaLink/Assets"
    build(False).save(f"{out_dir}/backdrop-light.jpg", quality=95, subsampling=0)
    build(True).save(f"{out_dir}/backdrop-dark.jpg", quality=95, subsampling=0)
    print("ok")
