#!/usr/bin/env python3
"""
qa_board.py — Generates a Multimodal Style-Consistency QA Contact Board.

Places the game's locked Style Anchors + Palette Swatch at the top, and renders all candidate
prepared 2D assets side-by-side on BOTH a dark transparency checker AND the game's actual
environment backdrop color (from art_bible.json) so any drift in line thickness, brushwork,
saturation, lighting angle, chroma halos, or silhouette scale jumps out immediately when
inspected with `read_file`.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


try:
    LANCZOS = Image.Resampling.LANCZOS
except AttributeError:
    LANCZOS = Image.LANCZOS  # type: ignore[attr-defined]


def parse_hex_rgb(hex_color: str) -> tuple[int, int, int]:
    h = hex_color.strip().lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


def make_checker(w: int, h: int, sq: int = 16) -> Image.Image:
    img = Image.new("RGBA", (w, h), (34, 36, 44, 255))
    draw = ImageDraw.Draw(img)
    for y in range(0, h, sq):
        for x in range(0, w, sq):
            if ((x // sq) + (y // sq)) % 2 == 1:
                draw.rectangle([x, y, x + sq - 1, y + sq - 1], fill=(48, 51, 62, 255))
    return img


def fit_inside(img: Image.Image, max_w: int, max_h: int) -> Image.Image:
    w, h = img.size
    if w == 0 or h == 0:
        return img
    scale = min(max_w / w, max_h / h)
    nw = max(1, int(round(w * scale)))
    nh = max(1, int(round(h * scale)))
    return img.resize((nw, nh), LANCZOS)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate side-by-side 2D Art Consistency QA Board")
    parser.add_argument("--bible", required=True, help="Path to art_bible.json")
    parser.add_argument("--assets", required=True, nargs="+", help="One or more PNG files or directories to review")
    parser.add_argument("--out", required=True, help="Output QA board PNG path")
    parser.add_argument("--cols", type=int, default=4)
    parser.add_argument("--cell", type=int, default=260, help="Square preview cell size in pixels")
    parser.add_argument("--title", default="Style Consistency QA Board")
    args = parser.parse_args()

    bible_path = Path(args.bible).resolve()
    bible = json.loads(bible_path.read_text(encoding="utf-8"))
    base = bible_path.parent

    # Gather asset PNGs
    asset_files: list[Path] = []
    for entry in args.assets:
        p = Path(entry).resolve()
        if p.is_file() and p.suffix.lower() == ".png":
            asset_files.append(p)
        elif p.is_dir():
            asset_files.extend(sorted(p.rglob("*.png")))

    if not asset_files:
        print("ERROR: No PNG assets found for QA board.", file=sys.stderr)
        return 1

    # Gather existing anchors
    anchors: list[tuple[str, Path]] = []
    for key in ("world", "characters", "ui"):
        rel = bible.get("anchors", {}).get(key)
        if rel:
            ap = base / rel
            if ap.exists():
                anchors.append((f"ANCHOR: {key}", ap))

    bg_mid = parse_hex_rgb(bible.get("palette", {}).get("bg_mid", "#2E2C42"))
    bg_deep = parse_hex_rgb(bible.get("palette", {}).get("bg_deep", "#1A1926"))

    try:
        font_h = ImageFont.load_default(size=18)
        font_s = ImageFont.load_default(size=13)
    except TypeError:
        font_h = ImageFont.load_default()
        font_s = ImageFont.load_default()

    pad = 16
    cell = args.cell
    cols = max(1, min(args.cols, len(asset_files)))
    rows = (len(asset_files) + cols - 1) // cols

    header_h = 56
    palette_h = 46
    anchor_h = 210 if anchors else 0
    grid_y = header_h + palette_h + (anchor_h + pad if anchors else 0) + 12

    board_w = max(860, pad + cols * (cell + pad))
    board_h = grid_y + rows * (cell + 34 + pad) + pad

    board = Image.new("RGBA", (board_w, board_h), (20, 21, 27, 255))
    draw = ImageDraw.Draw(board)

    # Header
    header_text = (
        f"{args.title} - {bible.get('game_name', 'Game')} "
        f"[{bible.get('preset', 'custom')} | {bible.get('projection', '')}]"
    )
    draw.text((pad, 14), header_text, fill=(242, 244, 250, 255), font=font_h)
    draw.text(
        (pad, 36),
        "Left half = alpha checker | Right half = in-game bg_mid swatch - check line weight, palette, lighting & halos",
        fill=(165, 172, 190, 255),
        font=font_s,
    )

    # Palette bar
    pal = list(bible.get("palette", {}).items())
    if pal:
        sw_w = (board_w - pad * 2) // len(pal)
        for i, (name, hex_v) in enumerate(pal):
            rgb = parse_hex_rgb(hex_v)
            x0 = pad + i * sw_w
            draw.rectangle([x0, header_h, x0 + sw_w - 4, header_h + 24], fill=rgb + (255,), outline=(80, 84, 96, 255))
            draw.text((x0 + 2, header_h + 27), name[:10], fill=(185, 190, 205, 255), font=font_s)

    # Anchor row
    if anchors:
        ay = header_h + palette_h + 8
        aw = min(320, (board_w - pad * (len(anchors) + 1)) // len(anchors))
        for i, (label, apath) in enumerate(anchors):
            ax = pad + i * (aw + pad)
            draw.rectangle([ax, ay, ax + aw, ay + anchor_h], fill=bg_deep + (255,), outline=(110, 180, 255, 255), width=2)
            aim = fit_inside(Image.open(apath).convert("RGBA"), aw - 12, anchor_h - 32)
            ox = ax + (aw - aim.width) // 2
            oy = ay + 24 + (anchor_h - 28 - aim.height) // 2
            board.alpha_composite(aim, (ox, oy))
            draw.text((ax + 8, ay + 5), label, fill=(140, 210, 255, 255), font=font_s)

    # Asset grid
    checker = make_checker(cell // 2, cell)
    game_bg = Image.new("RGBA", (cell - cell // 2, cell), bg_mid + (255,))

    for idx, fpath in enumerate(asset_files):
        r = idx // cols
        c = idx % cols
        x0 = pad + c * (cell + pad)
        y0 = grid_y + r * (cell + 34 + pad)

        # Split backdrop: left = alpha checker, right = game bg_mid
        board.paste(checker, (x0, y0))
        board.paste(game_bg, (x0 + cell // 2, y0))
        draw.rectangle([x0, y0, x0 + cell - 1, y0 + cell - 1], outline=(76, 82, 98, 255), width=1)

        im = Image.open(fpath).convert("RGBA")
        ow, oh = im.size
        thumb = fit_inside(im, cell - 16, cell - 16)
        tx = x0 + (cell - thumb.width) // 2
        ty = y0 + (cell - thumb.height) // 2
        board.alpha_composite(thumb, (tx, ty))

        label = f"{fpath.stem[:24]} ({ow}x{oh})"
        draw.text((x0 + 2, y0 + cell + 6), label, fill=(230, 232, 240, 255), font=font_s)

    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    board.save(out_path, "PNG")
    print(f"Wrote QA Consistency Board ({len(asset_files)} assets): {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
