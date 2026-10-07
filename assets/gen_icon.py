# -*- coding: utf-8 -*-
"""生成程序图标 assets/icon.ico（多尺寸，含 256x256）。"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon.ico")
S = 512
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 圆角方块 + 垂直渐变
pad = int(S * 0.055)
r = int(S * 0.22)
grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / float(S - 1)
    c = (int(70 + (24 - 70) * t), int(170 + (96 - 170) * t), int(255 + (232 - 255) * t), 255)
    gd.line([(0, y), (S, y)], fill=c)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=r, fill=255)
img.paste(grad, (0, 0), mask)

# 音符 ♪
glyph = "\u266a"
font = None
for path in (r"C:\Windows\Fonts\seguisym.ttf", r"C:\Windows\Fonts\segoeui.ttf",
             r"C:\Windows\Fonts\arial.ttf", r"C:\Windows\Fonts\msyh.ttc"):
    if os.path.exists(path):
        try:
            font = ImageFont.truetype(path, int(S * 0.66))
            break
        except Exception:
            font = None

if font is not None:
    bb = d.textbbox((0, 0), glyph, font=font)
    w, h = bb[2] - bb[0], bb[3] - bb[1]
    d.text(((S - w) / 2 - bb[0], (S - h) / 2 - bb[1] - S * 0.03), glyph, font=font, fill=(255, 255, 255, 255))
else:
    # 兜底：手绘音符（符头 + 符干 + 符尾）
    cx, cy = S * 0.42, S * 0.66
    rx, ry = S * 0.16, S * 0.115
    d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=(255, 255, 255, 255))
    d.rectangle([cx + rx * 0.72, cy - S * 0.42, cx + rx * 0.72 + S * 0.045, cy + ry * 0.4],
                fill=(255, 255, 255, 255))
    d.polygon([(cx + rx * 0.72 + S * 0.045, cy - S * 0.42),
               (S * 0.80, cy - S * 0.30),
               (S * 0.80, cy - S * 0.14),
               (cx + rx * 0.72 + S * 0.045, cy - S * 0.26)], fill=(255, 255, 255, 255))

img.save(OUT, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
print("icon written:", OUT, os.path.getsize(OUT), "bytes")
