"""生成 SudaLink 的玻璃底衬壁纸。

玻璃要被"看见"，背后必须有可被模糊的东西 —— 而且对比要够大：
底色和光斑的亮度差越大，光斑边缘穿过玻璃时"被磨平"的效果越明显。
但"有东西"不等于"有花纹"：这里所有结构都是大尺度软光斑，没有线条。

  aura   光场：饱和底色 + 大光斑，高对比。玻璃最明显（当前默认）
  plain  极简：近白到极浅蓝灰的纵向渐变，没有任何结构
  mist   雾面：大尺度柔和色块，最安静
  bloom  光晕：中间亮、四周沉，像磨砂亚克力背面打光
  solid  色阶：接近纯色的纵向色阶，最干净、最像"工具"
  silk   丝绸：宽而软的褶皱，保留一点结构感

用法：
  python gen_wallpaper.py <输出目录> [aura|plain|mist|bloom|solid|silk]（默认 aura）
"""
import math
import sys

import numpy as np
from PIL import Image, ImageFilter

W, H = 900, 1300


def hx(value):
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) / 255.0 for i in (0, 2, 4)], dtype=np.float32)


def ramp(stops, yn):
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


def blend(img, color, alpha):
    return img * (1.0 - alpha[..., None]) + color * alpha[..., None]


def blob(xn, yn, cx, cy, sx, sy):
    return np.exp(-(((xn - cx) ** 2) / (2 * sx ** 2) + ((yn - cy) ** 2) / (2 * sy ** 2)))


def mottle(seed, scale_px, blur_px):
    """多倍频柔和明度云纹：无结构、无方向，专供玻璃去"磨"。返回 -1..1 的场。"""
    rng = np.random.default_rng(seed)
    gw, gh = max(W // scale_px, 2) + 2, max(H // scale_px, 2) + 2
    grid = rng.normal(0.0, 1.0, (gh, gw)).astype(np.float32)
    grid = (grid - grid.min()) / (np.ptp(grid) + 1e-9)
    im = Image.fromarray((grid * 255).astype(np.uint8), "L") \
        .resize((W, H), Image.BICUBIC) \
        .filter(ImageFilter.GaussianBlur(blur_px))
    field = np.asarray(im, dtype=np.float32) / 255.0
    return (field - field.mean()) / (field.std() + 1e-9)


def build(style: str, dark: bool) -> Image.Image:
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    xn = xx / (W - 1)
    yn = yy / (H - 1)

    if style == "aura":
        # 高对比光场：饱和底色 + 大光斑。玻璃的"透"和"磨"都看得见。
        # 浅色主题：periwinkle/azure 中明度底 + 亮白/青/玫瑰光斑，面板提亮后明显浮出。
        stops = ([(0.00, hx("#0B0F22")), (0.40, hx("#131A3C")), (0.74, hx("#0D122B")), (1.00, hx("#070A18"))]
                 if dark else
                 [(0.00, hx("#8FA9EE")), (0.30, hx("#7E97E6")), (0.64, hx("#6E84D8")), (1.00, hx("#5C6EC2"))])
        img = ramp(stops, yn)
        if dark:
            img = blend(img, hx("#5FA8FF"), 0.55 * blob(xn, yn, 0.16, 0.06, 0.50, 0.34))
            img = blend(img, hx("#8C6BFF"), 0.42 * blob(xn, yn, 0.92, 0.30, 0.44, 0.36))
            img = blend(img, hx("#2FD4C4"), 0.30 * blob(xn, yn, 0.10, 0.86, 0.40, 0.32))
            img = blend(img, hx("#B8D8FF"), 0.24 * blob(xn, yn, 0.55, 0.42, 0.30, 0.24))
        else:
            img = blend(img, hx("#F4F8FF"), 0.85 * blob(xn, yn, 0.14, 0.02, 0.52, 0.34))
            img = blend(img, hx("#BFF0FF"), 0.60 * blob(xn, yn, 0.95, 0.38, 0.40, 0.34))
            img = blend(img, hx("#F8D9EA"), 0.55 * blob(xn, yn, 0.04, 0.72, 0.36, 0.30))
            img = blend(img, hx("#43549E"), 0.42 * blob(xn, yn, 0.80, 0.96, 0.44, 0.30))
            img = blend(img, hx("#CBE6FF"), 0.50 * blob(xn, yn, 0.55, 0.50, 0.26, 0.22))
        # 柔和明度云纹：20~70px 尺度的无结构起伏（约 ±5%）。
        # 没有它，光斑尺度都在几百 px，磨砂模糊前后没有差别，玻璃就"死"了。
        cloud = mottle(20261026, 46, 14) * 0.038 + mottle(20261027, 130, 40) * 0.05
        img = img * (1.0 + cloud)[..., None]
        grain = 0.006

    elif style == "plain":
        # 最素的一档：没有任何可辨认的结构。玻璃靠描边和阴影表达，不靠背景花纹。
        stops = ([(0.00, hx("#0E1119")), (0.42, hx("#131926")), (0.76, hx("#0D1220")), (1.00, hx("#080B12"))]
                 if dark else
                 [(0.00, hx("#FDFEFF")), (0.26, hx("#F3F6FB")), (0.62, hx("#E7ECF4")), (1.00, hx("#D8E0ED"))])
        img = ramp(stops, yn)
        img = blend(img, hx("#5D7FC4" if dark else "#FFFFFF"),
                    0.16 * blob(xn, yn, 0.22, 0.02, 0.60, 0.34))
        # 不再在左下角压冷色 —— 它会从卡片间隙透出来，被误认成脏阴影
        grain = 0.005 if not dark else 0.008

    elif style == "mist":
        stops = ([(0.00, hx("#070B16")), (0.38, hx("#101A33")), (0.72, hx("#0C1426")), (1.00, hx("#05080F"))]
                 if dark else
                 [(0.00, hx("#F4F9FF")), (0.26, hx("#E2EDFC")), (0.58, hx("#CBDCF5")), (1.00, hx("#AEC5E7"))])
        img = ramp(stops, yn)
        img = blend(img, hx("#8FB4FF" if dark else "#FFFFFF"),
                    0.42 * blob(xn, yn, 0.18, 0.05, 0.52, 0.34))
        img = blend(img, hx("#3A2A66" if dark else "#B9A8DE"),
                    0.22 * blob(xn, yn, 0.90, 0.78, 0.46, 0.30))
        img = blend(img, hx("#123055" if dark else "#9FC3E8"),
                    0.16 * blob(xn, yn, 0.05, 0.62, 0.34, 0.34))
        grain = 0.007

    elif style == "bloom":
        edge = hx("#04060E") if dark else hx("#5E7CAE")
        core = hx("#2A3C6E") if dark else hx("#F7FBFF")
        mid = hx("#101B38") if dark else hx("#9DBBE4")
        d = np.sqrt(((xn - 0.48) / 0.78) ** 2 + ((yn - 0.34) / 0.82) ** 2)
        t = np.clip(1.0 - d, 0.0, 1.0) ** 1.5
        img = blend(np.broadcast_to(edge, (H, W, 3)).copy(), mid, np.clip(t / 0.45, 0, 1))
        img = blend(img, core, (t ** 2.1) * (0.55 if dark else 0.95))
        img = blend(img, hx("#3B2C6B" if dark else "#8E7FCB"),
                    0.16 * blob(xn, yn, 0.98, 0.92, 0.40, 0.34))
        grain = 0.008

    elif style == "solid":
        stops = ([(0.00, hx("#0B1122")), (0.55, hx("#0E1730")), (1.00, hx("#070B18"))]
                 if dark else
                 [(0.00, hx("#DCE9FC")), (0.34, hx("#BCD3F4")), (0.68, hx("#9FBBE9")), (1.00, hx("#8AA8DC"))])
        img = ramp(stops, yn)
        img = blend(img, hx("#6E93D8" if dark else "#FFFFFF"),
                    0.20 * blob(xn, yn, 0.30, 0.10, 0.75, 0.55))
        grain = 0.011

    elif style == "silk":
        stops = ([(0.00, hx("#080C18")), (0.30, hx("#131E3C")), (0.62, hx("#1B2547")), (1.00, hx("#080C18"))]
                 if dark else
                 [(0.00, hx("#EFF6FF")), (0.28, hx("#CFE0F8")), (0.60, hx("#B3CBEE")), (1.00, hx("#93AFDA"))])
        img = ramp(stops, yn)
        # 宽而软的褶皱：靠低频起伏表达体积，不做细丝
        for index, (offset, amp, freq, strength) in enumerate(
                [(0.10, 0.10, 1.6, 0.34), (0.34, 0.13, 1.2, 0.26),
                 (0.62, 0.16, 1.9, 0.22), (0.86, 0.12, 1.4, 0.18)]):
            wave = offset + amp * np.sin(xn * freq * math.pi + index * 1.3) \
                   + 0.06 * np.sin(xn * 5.1 * math.pi + index * 2.1)
            ridge = np.exp(-((yn - wave) ** 2) / (2 * 0.085 ** 2))
            img = blend(img, hx("#7FA8F0" if dark else "#FFFFFF"), ridge * strength)
        grain = 0.009

    else:
        raise SystemExit(f"未知风格：{style}")

    # 边缘压暗，给上浮的卡片一个"地面"
    vig = 0.04 if style in ("plain", "aura") else (0.18 if dark else 0.10)
    img = img * (1.0 - vig * (((xn - 0.5) ** 2 + (yn - 0.5) ** 2) / 0.5) ** 1.2)[..., None]
    img = img * (1.0 - (0.05 if not dark else 0.10) *
                 np.clip((yn - 0.66) / 0.34, 0, 1)[..., None] ** 1.8)

    img = np.clip(img, 0.0, 1.0)
    out = Image.fromarray((img * 255).astype(np.uint8), "RGB")

    noise = np.random.default_rng(20261025).normal(0, grain * 255, (H, W, 1))
    arr = np.clip(np.asarray(out, dtype=np.float32) + noise, 0, 255).astype(np.uint8)
    return Image.fromarray(arr, "RGB").filter(ImageFilter.GaussianBlur(0.2))


if __name__ == "__main__":
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "src/SudaLink/Assets"
    style = sys.argv[2] if len(sys.argv) > 2 else "aura"
    build(style, False).save(f"{out_dir}/backdrop-light.jpg", quality=95, subsampling=0)
    build(style, True).save(f"{out_dir}/backdrop-dark.jpg", quality=95, subsampling=0)
    print(f"ok {style}")
