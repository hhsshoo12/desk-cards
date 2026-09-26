"""앱 아이콘(app.ico)을 그린다. 트레이 아이콘과 같은 2×2 색 타일 모양.

    python installer/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

COLORS = ["#4CC2FF", "#FFB900", "#6CCB5F", "#FF6F61"]
OUT = Path(__file__).resolve().parent.parent / "src" / "DeskFolders" / "app.ico"


def tile_image(size: int) -> Image.Image:
    # 크게 그려서 줄이면 가장자리가 부드럽다.
    big = size * 8
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    margin = big * 0.06
    gap = big * 0.08
    cell = (big - 2 * margin - gap) / 2
    radius = cell * 0.28
    for i, color in enumerate(COLORS):
        x = margin + (i % 2) * (cell + gap)
        y = margin + (i // 2) * (cell + gap)
        d.rounded_rectangle([x, y, x + cell, y + cell], radius=radius, fill=color)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [tile_image(s) for s in sizes]
    images[-1].save(OUT, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
