#!/usr/bin/env python3
"""
image_prep.py — Deterministic 2D HD game art post-processing & quality audit toolkit.

Capabilities:
  - to-png  : Lossless JPG/WebP -> PNG conversion into raw_gen/
  - cutout  : Border flood-fill chroma removal + interior hole keying + 3px edge-band despill
              + tight trim + padding + optional consistent texel-density resize
              (also supports delegating to BringRig2D prep when --bring-rig2d-proj is provided)
  - tile    : Seamless tile verification (seam energy score + 2x2 preview), 50% wrap-offset
              generation for AI seam repainting, and optional deterministic edge cross-blend
  - slice9  : Auto-detects Bring Engine 9-slice insets [L, B, R, T] and renders stretch QA preview
  - audit   : Audits prepared PNGs for chroma halo leakage, alpha cleanliness, palette alignment,
              and background-vs-foreground contrast hierarchy
"""

from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from collections import deque
from pathlib import Path
from typing import Any

from PIL import Image, ImageChops, ImageDraw, ImageFont

try:
    LANCZOS = Image.Resampling.LANCZOS
except AttributeError:
    LANCZOS = Image.LANCZOS  # type: ignore[attr-defined]


def parse_hex_rgb(hex_color: str) -> tuple[int, int, int]:
    h = hex_color.strip().lstrip("#")
    if len(h) != 6:
        raise ValueError(f"Invalid hex color: {hex_color}")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


def detect_key_color(img: Image.Image, mode: str) -> tuple[int, int, int]:
    m = mode.lower().strip()
    if m == "green":
        return (0, 255, 0)
    if m == "magenta":
        return (255, 0, 255)
    if m == "black":
        return (0, 0, 0)
    if m.startswith("#") and len(m) == 7:
        return parse_hex_rgb(m)
    # auto: median of border corner samples
    rgb = img.convert("RGB")
    w, h = rgb.size
    pts = [
        (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1),
        (w // 2, 0), (w // 2, h - 1), (0, h // 2), (w - 1, h // 2),
        (4, 4), (w - 5, 4), (4, h - 5), (w - 5, h - 5),
    ]
    rs, gs, bs = [], [], []
    for x, y in pts:
        cx = max(0, min(w - 1, x))
        cy = max(0, min(h - 1, y))
        r, g, b = rgb.getpixel((cx, cy))
        rs.append(r)
        gs.append(g)
        bs.append(b)
    rs.sort()
    gs.sort()
    bs.sort()
    mid = len(rs) // 2
    return (rs[mid], gs[mid], bs[mid])


def color_dist(r: int, g: int, b: int, kr: int, kg: int, kb: int) -> float:
    dr = r - kr
    dg = g - kg
    db = b - kb
    return math.sqrt(dr * dr + dg * dg + db * db)


def cutout_single_python(
    src_path: Path,
    dst_path: Path,
    key_mode: str,
    tolerance: float,
    feather: float,
    despill: bool,
    trim: bool,
    pad: int,
    max_width: int | None,
    max_height: int | None,
    target_height: int | None,
) -> dict[str, Any]:
    img = Image.open(src_path).convert("RGBA")
    w, h = img.size
    kr, kg, kb = detect_key_color(img, key_mode)

    buf = bytearray(img.tobytes())
    n = w * h
    bg_mask = bytearray(n)  # 1 = exterior background flood-filled
    visited = bytearray(n)

    tol_outer = tolerance + feather
    tol_sq = tol_outer * tol_outer
    hole_tol = max(24.0, tolerance * 0.88)
    hole_sq = hole_tol * hole_tol

    # 1. Seed BFS from image border pixels near key color
    q: deque[int] = deque()

    def try_seed(idx: int) -> None:
        if visited[idx]:
            return
        o = idx * 4
        dr = buf[o] - kr
        dg = buf[o + 1] - kg
        db = buf[o + 2] - kb
        if dr * dr + dg * dg + db * db <= tol_sq:
            visited[idx] = 1
            bg_mask[idx] = 1
            q.append(idx)

    for x in range(w):
        try_seed(x)
        try_seed((h - 1) * w + x)
    for y in range(1, h - 1):
        try_seed(y * w)
        try_seed(y * w + (w - 1))

    while q:
        idx = q.popleft()
        x = idx % w
        y = idx // w
        # 4-connected flood fill
        if x > 0:
            ni = idx - 1
            if not visited[ni]:
                o = ni * 4
                dr = buf[o] - kr
                dg = buf[o + 1] - kg
                db = buf[o + 2] - kb
                if dr * dr + dg * dg + db * db <= tol_sq:
                    visited[ni] = 1
                    bg_mask[ni] = 1
                    q.append(ni)
        if x + 1 < w:
            ni = idx + 1
            if not visited[ni]:
                o = ni * 4
                dr = buf[o] - kr
                dg = buf[o + 1] - kg
                db = buf[o + 2] - kb
                if dr * dr + dg * dg + db * db <= tol_sq:
                    visited[ni] = 1
                    bg_mask[ni] = 1
                    q.append(ni)
        if y > 0:
            ni = idx - w
            if not visited[ni]:
                o = ni * 4
                dr = buf[o] - kr
                dg = buf[o + 1] - kg
                db = buf[o + 2] - kb
                if dr * dr + dg * dg + db * db <= tol_sq:
                    visited[ni] = 1
                    bg_mask[ni] = 1
                    q.append(ni)
        if y + 1 < h:
            ni = idx + w
            if not visited[ni]:
                o = ni * 4
                dr = buf[o] - kr
                dg = buf[o + 1] - kg
                db = buf[o + 2] - kb
                if dr * dr + dg * dg + db * db <= tol_sq:
                    visited[ni] = 1
                    bg_mask[ni] = 1
                    q.append(ni)

    # 2. Tight interior chroma hole keying (handles gaps inside bows, legs, UI rings)
    # Only key strongly matching chroma pixels inside the silhouette so natural interior colors survive.
    for idx in range(n):
        if bg_mask[idx]:
            continue
        o = idx * 4
        dr = buf[o] - kr
        dg = buf[o + 1] - kg
        db = buf[o + 2] - kb
        if dr * dr + dg * dg + db * db <= hole_sq:
            bg_mask[idx] = 1

    # 3. Compute soft alpha for background + 2px edge band
    edge_band = bytearray(n)
    for idx in range(n):
        if bg_mask[idx]:
            o = idx * 4
            dist = color_dist(buf[o], buf[o + 1], buf[o + 2], kr, kg, kb)
            if dist <= tolerance or feather <= 0.0:
                buf[o + 3] = 0
            else:
                t = max(0.0, min(1.0, (dist - tolerance) / feather))
                buf[o + 3] = int(round(t * buf[o + 3]))
                edge_band[idx] = 1

    # Mark 3px inward neighbor ring of transparent pixels for targeted edge-band despill
    if despill:
        cur_ring = [idx for idx in range(n) if buf[idx * 4 + 3] < 245]
        for _step in range(3):
            next_ring: list[int] = []
            for idx in cur_ring:
                x = idx % w
                y = idx // w
                for ni in (
                    idx - 1 if x > 0 else -1,
                    idx + 1 if x + 1 < w else -1,
                    idx - w if y > 0 else -1,
                    idx + w if y + 1 < h else -1,
                ):
                    if ni >= 0 and not edge_band[ni] and buf[ni * 4 + 3] > 0:
                        edge_band[ni] = 1
                        next_ring.append(ni)
            cur_ring = next_ring

        is_green = kg > kr + 60 and kg > kb + 60
        is_magenta = kr > kg + 60 and kb > kg + 60
        for idx in range(n):
            if not edge_band[idx]:
                continue
            o = idx * 4
            a = buf[o + 3]
            if a == 0:
                buf[o] = 0
                buf[o + 1] = 0
                buf[o + 2] = 0
                continue
            r, g, b = buf[o], buf[o + 1], buf[o + 2]
            if is_green:
                limit = max(r, b)
                if g > limit:
                    spill = g - limit
                    buf[o + 1] = limit
                    # Subtle neutral lift on dark edges so despill doesn't create harsh black crust
                    if a < 220:
                        buf[o + 3] = max(0, int(a - spill * 0.25))
            elif is_magenta:
                avg_rb = (r + b) // 2
                if avg_rb > g + 25:
                    excess = min(r - g, b - g)
                    if excess > 0:
                        buf[o] = max(0, r - excess)
                        buf[o + 2] = max(0, b - excess)
                        if a < 220:
                            buf[o + 3] = max(0, int(a - excess * 0.25))

    out_img = Image.frombytes("RGBA", (w, h), bytes(buf))

    # 4. Trim & pad
    if trim:
        alpha = out_img.getchannel("A")
        bbox = alpha.point(lambda p: 255 if p > 6 else 0).getbbox()
        if bbox:
            out_img = out_img.crop(bbox)
            if pad > 0:
                padded = Image.new("RGBA", (out_img.width + pad * 2, out_img.height + pad * 2), (0, 0, 0, 0))
                padded.paste(out_img, (pad, pad))
                out_img = padded

    # 5. Consistent texel-density resize if requested
    ow, oh = out_img.size
    scale = 1.0
    if target_height and oh > 0:
        scale = min(scale, target_height / oh) if max_height else (target_height / oh)
    if max_width and ow * scale > max_width:
        scale = min(scale, max_width / ow)
    if max_height and oh * scale > max_height:
        scale = min(scale, max_height / oh)

    if abs(scale - 1.0) > 1e-4:
        nw = max(1, int(round(ow * scale)))
        nh = max(1, int(round(oh * scale)))
        out_img = out_img.resize((nw, nh), LANCZOS)

    dst_path.parent.mkdir(parents=True, exist_ok=True)
    out_img.save(dst_path, "PNG")
    return {
        "name": dst_path.stem,
        "source": str(src_path),
        "output": str(dst_path),
        "width": out_img.width,
        "height": out_img.height,
        "key_rgb": [kr, kg, kb],
    }


def cmd_to_png(args: argparse.Namespace) -> int:
    src = Path(args.input).resolve()
    dst = Path(args.output).resolve()
    if src.is_file():
        dst.parent.mkdir(parents=True, exist_ok=True)
        out_file = dst if dst.suffix.lower() == ".png" else (dst / f"{src.stem}.png")
        Image.open(src).convert("RGBA").save(out_file, "PNG")
        print(f"Converted: {src.name} -> {out_file}")
        return 0

    dst.mkdir(parents=True, exist_ok=True)
    exts = {".jpg", ".jpeg", ".webp", ".bmp", ".png"}
    count = 0
    for f in sorted(src.iterdir()):
        if f.is_file() and f.suffix.lower() in exts:
            out_file = dst / f"{f.stem}.png"
            Image.open(f).convert("RGBA").save(out_file, "PNG")
            count += 1
    print(f"Converted {count} image(s) into {dst}")
    return 0


def cmd_cutout(args: argparse.Namespace) -> int:
    src = Path(args.input).resolve()
    dst = Path(args.output).resolve()

    # Optional fast C# delegation to BringRig2D prep when requested
    if args.bring_rig2d_proj:
        proj = Path(args.bring_rig2d_proj).resolve()
        cmd = [
            "dotnet", "run", "--project", str(proj), "-c", "Release", "--no-build", "--",
            "prep", "--input", str(src), "--output", str(dst),
            "--key", args.key,
            "--tolerance", str(args.tolerance),
            "--feather", str(args.feather),
            "--pad", str(args.pad),
        ]
        if args.despill:
            cmd.append("--despill")
        else:
            cmd.append("--no-despill")
        if args.trim:
            cmd.append("--trim")
        else:
            cmd.append("--no-trim")
        if args.manifest:
            cmd.extend(["--manifest", str(Path(args.manifest).resolve())])
        res = subprocess.run(cmd)
        if res.returncode != 0:
            print("WARN: BringRig2D prep failed; falling back to Python cutout.", file=sys.stderr)
        else:
            # Apply optional post-resize if requested
            if args.max_width or args.max_height or args.target_height:
                files = [dst] if dst.is_file() else sorted(dst.glob("*.png"))
                for f in files:
                    im = Image.open(f).convert("RGBA")
                    ow, oh = im.size
                    scale = (args.target_height / oh) if args.target_height else 1.0
                    if args.max_width and ow * scale > args.max_width:
                        scale = min(scale, args.max_width / ow)
                    if args.max_height and oh * scale > args.max_height:
                        scale = min(scale, args.max_height / oh)
                    if abs(scale - 1.0) > 1e-4:
                        im.resize((max(1, int(round(ow * scale))), max(1, int(round(oh * scale)))), LANCZOS).save(f, "PNG")
            return 0

    records: list[dict[str, Any]] = []
    if src.is_file():
        out_file = dst if dst.suffix.lower() == ".png" else (dst / f"{src.stem}.png")
        rec = cutout_single_python(
            src, out_file, args.key, args.tolerance, args.feather,
            args.despill, args.trim, args.pad,
            args.max_width, args.max_height, args.target_height,
        )
        records.append(rec)
    else:
        dst.mkdir(parents=True, exist_ok=True)
        for f in sorted(src.iterdir()):
            if f.is_file() and f.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
                out_file = dst / f"{f.stem}.png"
                rec = cutout_single_python(
                    f, out_file, args.key, args.tolerance, args.feather,
                    args.despill, args.trim, args.pad,
                    args.max_width, args.max_height, args.target_height,
                )
                records.append(rec)

    if args.manifest:
        mpath = Path(args.manifest).resolve()
        mpath.parent.mkdir(parents=True, exist_ok=True)
        mpath.write_text(json.dumps({"parts": records}, indent=2) + "\n", encoding="utf-8")

    for r in records:
        print(f"Cutout: {r['name']} -> {r['width']}x{r['height']} (key={r['key_rgb']})")
    return 0


def compute_seam_energy(img: Image.Image, axis: str) -> dict[str, float]:
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    res: dict[str, float] = {}

    if axis in ("x", "xy"):
        total = 0.0
        for y in range(h):
            r0, g0, b0 = px[0, y]
            r1, g1, b1 = px[w - 1, y]
            total += math.sqrt((r0 - r1) ** 2 + (g0 - g1) ** 2 + (b0 - b1) ** 2)
        res["seam_x"] = round(total / max(1, h), 2)

    if axis in ("y", "xy"):
        total = 0.0
        for x in range(w):
            r0, g0, b0 = px[x, 0]
            r1, g1, b1 = px[x, h - 1]
            total += math.sqrt((r0 - r1) ** 2 + (g0 - g1) ** 2 + (b0 - b1) ** 2)
        res["seam_y"] = round(total / max(1, w), 2)

    return res


def cross_blend_axis(img: Image.Image, blend_px: int, horizontal: bool) -> Image.Image:
    """
    C1-continuous overlap-and-crop wrap blend for seamless tiling on painted textures.
    Folds the trailing strip `[len-b : len]` over `[0 : b]` with a cosine ramp from 1->0,
    crops the folded strip so `x=0` continues directly from `x=len-b-1`, and resizes cleanly
    back to the original dimensions. Zero seam discontinuity at both `0` and `b`.
    """
    rgba = img.convert("RGBA")
    w, h = rgba.size
    dim = w if horizontal else h
    b = max(4, min(blend_px, dim // 3))

    if horizontal:
        nw = w - b
        body = rgba.crop((0, 0, nw, h))
        tail = rgba.crop((nw, 0, w, h))
        body_px = body.load()
        tail_px = tail.load()
        for x in range(b):
            # Cosine ramp: alpha=1 at x=0 (exact continuation of nw-1), alpha=0 at x=b-1 (exact match to x=b)
            t = 0.5 * (1.0 + math.cos(math.pi * (x + 0.5) / b))
            inv = 1.0 - t
            for y in range(h):
                c0 = body_px[x, y]
                c1 = tail_px[x, y]
                body_px[x, y] = (
                    int(round(c1[0] * t + c0[0] * inv)),
                    int(round(c1[1] * t + c0[1] * inv)),
                    int(round(c1[2] * t + c0[2] * inv)),
                    int(round(c1[3] * t + c0[3] * inv)),
                )
        return body.resize((w, h), LANCZOS)
    else:
        nh = h - b
        body = rgba.crop((0, 0, w, nh))
        tail = rgba.crop((0, nh, w, h))
        body_px = body.load()
        tail_px = tail.load()
        for y in range(b):
            t = 0.5 * (1.0 + math.cos(math.pi * (y + 0.5) / b))
            inv = 1.0 - t
            for x in range(w):
                c0 = body_px[x, y]
                c1 = tail_px[x, y]
                body_px[x, y] = (
                    int(round(c1[0] * t + c0[0] * inv)),
                    int(round(c1[1] * t + c0[1] * inv)),
                    int(round(c1[2] * t + c0[2] * inv)),
                    int(round(c1[3] * t + c0[3] * inv)),
                )
        return body.resize((w, h), LANCZOS)


def cmd_tile(args: argparse.Namespace) -> int:
    src = Path(args.input).resolve()
    img = Image.open(src).convert("RGBA")

    if args.size:
        img = img.resize((args.size, args.size), LANCZOS)

    before = compute_seam_energy(img, args.axis)

    if args.auto_blend > 0:
        if args.axis in ("x", "xy"):
            img = cross_blend_axis(img, args.auto_blend, horizontal=True)
        if args.axis in ("y", "xy"):
            img = cross_blend_axis(img, args.auto_blend, horizontal=False)

    after = compute_seam_energy(img, args.axis)

    if args.output:
        out_path = Path(args.output).resolve()
        out_path.parent.mkdir(parents=True, exist_ok=True)
        img.save(out_path, "PNG")
        print(f"Saved tile: {out_path} ({img.width}x{img.height})")

    if args.offset_out:
        off_x = img.width // 2 if args.axis in ("x", "xy") else 0
        off_y = img.height // 2 if args.axis in ("y", "xy") else 0
        shifted = ImageChops.offset(img, off_x, off_y)
        off_path = Path(args.offset_out).resolve()
        off_path.parent.mkdir(parents=True, exist_ok=True)
        shifted.save(off_path, "PNG")
        print(f"Saved 50%-offset seam view: {off_path}")

    if args.preview:
        w, h = img.size
        cols = 2 if args.axis in ("x", "xy") else 1
        rows = 2 if args.axis in ("y", "xy") else 1
        prev = Image.new("RGBA", (w * cols, h * rows), (0, 0, 0, 0))
        for r in range(rows):
            for c in range(cols):
                prev.paste(img, (c * w, r * h))
        # Draw subtle dashed red seam markers ONLY at outer margin tick marks (not over art)
        draw = ImageDraw.Draw(prev)
        if cols == 2:
            draw.line([(w, 0), (w, 18)], fill=(255, 70, 70, 220), width=2)
            draw.line([(w, prev.height - 18), (w, prev.height)], fill=(255, 70, 70, 220), width=2)
        if rows == 2:
            draw.line([(0, h), (18, h)], fill=(255, 70, 70, 220), width=2)
            draw.line([(prev.width - 18, h), (prev.width, h)], fill=(255, 70, 70, 220), width=2)
        prev_path = Path(args.preview).resolve()
        prev_path.parent.mkdir(parents=True, exist_ok=True)
        prev.save(prev_path, "PNG")
        print(f"Saved tiled preview: {prev_path}")

    print(f"Seam energy BEFORE: {before} | AFTER: {after} (lower is cleaner; <18 is very seamless)")
    return 0


def estimate_slice9(img: Image.Image) -> list[int]:
    """
    Estimate Bring Engine 9-slice insets [Left, Bottom, Right, Top] in pixels.
    Detects where horizontal/vertical row-to-row or col-to-col variance stabilizes in the center band.
    """
    rgba = img.convert("RGBA")
    w, h = rgba.size
    px = rgba.load()

    # Column variance in the middle 40% vertical band
    y0, y1 = max(0, int(h * 0.3)), min(h, int(h * 0.7))
    col_diff = [0.0] * w
    for x in range(1, w):
        s = 0.0
        for y in range(y0, y1):
            a = px[x, y]
            b = px[x - 1, y]
            s += abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2]) + abs(a[3] - b[3])
        col_diff[x] = s / max(1, y1 - y0)

    x0, x1 = max(0, int(w * 0.3)), min(w, int(w * 0.7))
    row_diff = [0.0] * h
    for y in range(1, h):
        s = 0.0
        for x in range(x0, x1):
            a = px[x, y]
            b = px[x, y - 1]
            s += abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2]) + abs(a[3] - b[3])
        row_diff[y] = s / max(1, x1 - x0)

    thresh_x = max(14.0, sum(col_diff[w // 3 : 2 * w // 3]) / max(1, w // 3) * 2.2)
    thresh_y = max(14.0, sum(row_diff[h // 3 : 2 * h // 3]) / max(1, h // 3) * 2.2)

    min_border_x = max(8, int(w * 0.12))
    max_border_x = max(min_border_x + 2, int(w * 0.38))
    min_border_y = max(8, int(h * 0.12))
    max_border_y = max(min_border_y + 2, int(h * 0.38))

    left = min_border_x
    for x in range(min_border_x, max_border_x):
        if col_diff[x] > thresh_x:
            left = x + 4
    right_from_edge = min_border_x
    for x in range(w - min_border_x, w - max_border_x, -1):
        if col_diff[x] > thresh_x:
            right_from_edge = (w - x) + 4

    top = min_border_y
    for y in range(min_border_y, max_border_y):
        if row_diff[y] > thresh_y:
            top = y + 4
    bottom_from_edge = min_border_y
    for y in range(h - min_border_y, h - max_border_y, -1):
        if row_diff[y] > thresh_y:
            bottom_from_edge = (h - y) + 4

    left = min(left, max_border_x)
    right_from_edge = min(right_from_edge, max_border_x)
    top = min(top, max_border_y)
    bottom_from_edge = min(bottom_from_edge, max_border_y)

    # Bring Engine pack.json convention: [Left, Bottom, Right, Top]
    return [int(left), int(bottom_from_edge), int(right_from_edge), int(top)]


def render_9slice(src: Image.Image, borders: list[int], target_w: int, target_h: int) -> Image.Image:
    left, bottom, right, top = borders
    sw, sh = src.size
    left = min(left, sw // 2 - 1)
    right = min(right, sw // 2 - 1)
    top = min(top, sh // 2 - 1)
    bottom = min(bottom, sh // 2 - 1)

    mid_sw = max(1, sw - left - right)
    mid_sh = max(1, sh - top - bottom)
    mid_tw = max(1, target_w - left - right)
    mid_th = max(1, target_h - top - bottom)

    out = Image.new("RGBA", (target_w, target_h), (0, 0, 0, 0))
    # 9 regions (PIL uses top-down y)
    xs_s = [0, left, sw - right, sw]
    ys_s = [0, top, sh - bottom, sh]
    xs_t = [0, left, target_w - right, target_w]
    ys_t = [0, top, target_h - bottom, target_h]

    for r in range(3):
        for c in range(3):
            crop = src.crop((xs_s[c], ys_s[r], xs_s[c + 1], ys_s[r + 1]))
            tw = max(1, xs_t[c + 1] - xs_t[c])
            th = max(1, ys_t[r + 1] - ys_t[r])
            if crop.size != (tw, th):
                crop = crop.resize((tw, th), LANCZOS)
            out.paste(crop, (xs_t[c], ys_t[r]))
    _ = (mid_sw, mid_sh, mid_tw, mid_th)
    return out


def cmd_slice9(args: argparse.Namespace) -> int:
    src_path = Path(args.input).resolve()
    img = Image.open(src_path).convert("RGBA")
    if args.borders:
        borders = [int(x.strip()) for x in args.borders.split(",")]
        if len(borders) != 4:
            print("ERROR: --borders must be L,B,R,T (4 integers)", file=sys.stderr)
            return 2
    else:
        borders = estimate_slice9(img)

    print(f"9-Slice borders [Left, Bottom, Right, Top] for {src_path.name} ({img.width}x{img.height}): {borders}")

    if args.preview:
        l, b, r, t = borders
        sw, sh = img.size
        # 1) Guide overlay on original
        guide = img.copy()
        gd = ImageDraw.Draw(guide)
        gd.line([(l, 0), (l, sh)], fill=(0, 255, 255, 220), width=2)
        gd.line([(sw - r, 0), (sw - r, sh)], fill=(0, 255, 255, 220), width=2)
        gd.line([(0, t), (sw, t)], fill=(255, 220, 0, 220), width=2)
        gd.line([(0, sh - b), (sw, sh - b)], fill=(255, 220, 0, 220), width=2)

        wide = render_9slice(img, borders, int(sw * 2.2), sh)
        tall = render_9slice(img, borders, sw, int(sh * 1.9))

        pad = 24
        cw = max(sw + wide.width + pad * 3, tall.width + pad * 2)
        ch = max(sh, wide.height) + tall.height + pad * 3 + 36
        board = Image.new("RGBA", (cw, ch), (28, 30, 38, 255))
        bd = ImageDraw.Draw(board)
        bd.text((pad, 10), f"9-Slice QA: {src_path.name} | slice [L,B,R,T]={borders}", fill=(240, 240, 245, 255))
        board.alpha_composite(guide, (pad, 36))
        board.alpha_composite(wide, (pad * 2 + sw, 36))
        board.alpha_composite(tall, (pad, 36 + max(sh, wide.height) + pad))

        prev_path = Path(args.preview).resolve()
        prev_path.parent.mkdir(parents=True, exist_ok=True)
        board.save(prev_path, "PNG")
        print(f"Saved 9-slice QA preview: {prev_path}")
    return 0


def cmd_audit(args: argparse.Namespace) -> int:
    target = Path(args.input).resolve()
    files = [target] if target.is_file() else sorted(target.rglob("*.png"))
    if not files:
        print(f"ERROR: No PNG files found under {target}", file=sys.stderr)
        return 1

    palette_rgbs: list[tuple[int, int, int]] = []
    if args.bible:
        bible = json.loads(Path(args.bible).resolve().read_text(encoding="utf-8"))
        for hex_val in bible.get("palette", {}).values():
            palette_rgbs.append(parse_hex_rgb(hex_val))

    reports: list[dict[str, Any]] = []
    any_warn = False

    for f in files:
        img = Image.open(f).convert("RGBA")
        w, h = img.size
        data = img.tobytes()
        opaque = 0
        green_halo = 0
        magenta_halo = 0
        lum_sum = 0.0
        sat_sum = 0.0
        pal_dist_sum = 0.0
        sample_count = 0

        # Sample step to stay instantaneous even on 4K images
        step = max(1, (w * h) // 65536)
        for idx in range(0, w * h, step):
            o = idx * 4
            r, g, b, a = data[o], data[o + 1], data[o + 2], data[o + 3]
            if a < 10:
                continue
            opaque += 1
            if g > max(r, b) + 45 and g > 130:
                green_halo += 1
            if r > g + 45 and b > g + 45 and r > 130 and b > 130:
                magenta_halo += 1

            lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0
            mx = max(r, g, b)
            mn = min(r, g, b)
            sat = (mx - mn) / mx if mx > 0 else 0.0
            lum_sum += lum
            sat_sum += sat
            sample_count += 1

            if palette_rgbs:
                best = min(
                    math.sqrt((r - pr) ** 2 + (g - pg) ** 2 + (b - pb) ** 2)
                    for pr, pg, pb in palette_rgbs
                )
                pal_dist_sum += best

        halo_g_pct = round(100.0 * green_halo / max(1, opaque), 2)
        halo_m_pct = round(100.0 * magenta_halo / max(1, opaque), 2)
        mean_lum = round(lum_sum / max(1, sample_count), 3)
        mean_sat = round(sat_sum / max(1, sample_count), 3)
        mean_pal_dist = round(pal_dist_sum / max(1, sample_count), 1) if palette_rgbs else None

        warnings: list[str] = []
        if opaque == 0:
            warnings.append("empty-alpha")
        if halo_g_pct > 2.5:
            warnings.append(f"green-chroma-halo({halo_g_pct}%)")
        if halo_m_pct > 2.5:
            warnings.append(f"magenta-chroma-halo({halo_m_pct}%)")
        if mean_pal_dist is not None and mean_pal_dist > 82.0:
            warnings.append(f"palette-drift(avgDist={mean_pal_dist})")
        if warnings:
            any_warn = True

        rep = {
            "file": str(f.relative_to(target)) if target.is_dir() else f.name,
            "width": w,
            "height": h,
            "mean_luminance": mean_lum,
            "mean_saturation": mean_sat,
            "green_halo_pct": halo_g_pct,
            "magenta_halo_pct": halo_m_pct,
            "palette_avg_dist": mean_pal_dist,
            "warnings": warnings,
        }
        reports.append(rep)
        status = "WARN" if warnings else "OK  "
        print(
            f"[{status}] {rep['file']:<32} {w:4d}x{h:<4d} lum={mean_lum:.2f} sat={mean_sat:.2f} "
            f"haloG={halo_g_pct:.1f}% haloM={halo_m_pct:.1f}%"
            + (f" palDist={mean_pal_dist}" if mean_pal_dist is not None else "")
            + (f"  !! {', '.join(warnings)}" if warnings else "")
        )

    if args.json_out:
        jp = Path(args.json_out).resolve()
        jp.parent.mkdir(parents=True, exist_ok=True)
        jp.write_text(json.dumps({"assets": reports}, indent=2) + "\n", encoding="utf-8")

    return 0 if not (args.strict and any_warn) else 1


def main() -> int:
    parser = argparse.ArgumentParser(description="Bring 2D Art deterministic prep & QA toolkit")
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_png = sub.add_parser("to-png", help="Convert JPG/WebP images to lossless PNG")
    p_png.add_argument("--input", "-i", required=True)
    p_png.add_argument("--output", "-o", required=True)

    p_cut = sub.add_parser("cutout", help="Chroma-key + edge despill + trim + optional resize")
    p_cut.add_argument("--input", "-i", required=True)
    p_cut.add_argument("--output", "-o", required=True)
    p_cut.add_argument("--key", default="auto", help="auto | green | magenta | black | #RRGGBB")
    p_cut.add_argument("--tolerance", type=float, default=65.0)
    p_cut.add_argument("--feather", type=float, default=28.0)
    p_cut.add_argument("--despill", dest="despill", action="store_true", default=True)
    p_cut.add_argument("--no-despill", dest="despill", action="store_false")
    p_cut.add_argument("--trim", dest="trim", action="store_true", default=True)
    p_cut.add_argument("--no-trim", dest="trim", action="store_false")
    p_cut.add_argument("--pad", type=int, default=4)
    p_cut.add_argument("--max-width", type=int)
    p_cut.add_argument("--max-height", type=int)
    p_cut.add_argument("--target-height", type=int)
    p_cut.add_argument("--manifest", help="Optional JSON manifest path")
    p_cut.add_argument("--bring-rig2d-proj", help="Optional path to BringRig2D.csproj to accelerate prep")

    p_tile = sub.add_parser("tile", help="Check/blend seamless tiles and render 2x2 QA preview")
    p_tile.add_argument("--input", "-i", required=True)
    p_tile.add_argument("--output", "-o", help="Output PNG path")
    p_tile.add_argument("--axis", default="xy", choices=["x", "y", "xy"])
    p_tile.add_argument("--size", type=int, help="Optional square resize target (e.g. 256 or 512)")
    p_tile.add_argument("--auto-blend", type=int, default=0, help="Edge cross-blend width in pixels (e.g. 24-48)")
    p_tile.add_argument("--offset-out", help="Write 50%-shifted image so center seam can be inspected/repainted")
    p_tile.add_argument("--preview", help="Write 2x2 (or 2x1) tiled QA preview PNG")

    p_s9 = sub.add_parser("slice9", help="Auto-detect [L,B,R,T] 9-slice borders & render stretch QA")
    p_s9.add_argument("--input", "-i", required=True)
    p_s9.add_argument("--borders", help="Manual override L,B,R,T")
    p_s9.add_argument("--preview", help="Write 9-slice stretch QA PNG")

    p_aud = sub.add_parser("audit", help="Audit PNGs for halos, alpha, luminance & palette alignment")
    p_aud.add_argument("--input", "-i", required=True)
    p_aud.add_argument("--bible", help="Optional art_bible.json for palette alignment scoring")
    p_aud.add_argument("--json-out", help="Optional JSON report path")
    p_aud.add_argument("--strict", action="store_true", help="Exit 1 if any warning occurs")

    args = parser.parse_args()
    if args.cmd == "to-png":
        return cmd_to_png(args)
    if args.cmd == "cutout":
        return cmd_cutout(args)
    if args.cmd == "tile":
        return cmd_tile(args)
    if args.cmd == "slice9":
        return cmd_slice9(args)
    if args.cmd == "audit":
        return cmd_audit(args)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
