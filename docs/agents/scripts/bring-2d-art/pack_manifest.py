#!/usr/bin/env python3
"""
pack_manifest.py — Generate/update Bring Engine `pack.json` for a `raw_assets/<bundle>/` folder
and optionally run `BringPacker` to emit a zero-loose-texture BC1/BC3 `.bpack` bundle.

Matches Bring Engine `docs/agents/13-asset-compression-atlases-and-9slice.md` schema exactly.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

from PIL import Image

from image_prep import estimate_slice9


def norm_rel(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def stem_name(root: Path, path: Path) -> str:
    rel = path.relative_to(root).with_suffix("")
    return rel.as_posix()


def main() -> int:
    parser = argparse.ArgumentParser(description="Build/merge Bring Engine pack.json & optionally run BringPacker")
    parser.add_argument("--bundle-dir", required=True, help="Target raw_assets/<bundle>/ directory")
    parser.add_argument("--compression", default="brotli", choices=["brotli", "lz4", "none"])
    parser.add_argument("--max-atlas", type=int, default=2048)
    parser.add_argument("--padding", type=int, default=2)
    parser.add_argument(
        "--wrap-glob",
        action="append",
        default=[],
        help="Relative path substring(s) that should use atlas=false, wrap=true (e.g. 'tiles/' or 'parallax/sky')",
    )
    parser.add_argument(
        "--slice9",
        action="append",
        default=[],
        help="Explicit 9-slice entry 'rel/path.png=L,B,R,T' or 'rel/path.png=auto'",
    )
    parser.add_argument(
        "--bottom-pivot-glob",
        action="append",
        default=["props/", "characters/", "enemies/"],
        help="Subfolder prefix(es) whose sprites get bottom-center pivot [0.5, 0.0]",
    )
    parser.add_argument("--pack-out", help="Optional output .bpack path to compile immediately with BringPacker")
    parser.add_argument("--bring-packer-proj", help="Path to BringPacker.csproj (required with --pack-out)")
    args = parser.parse_args()

    bundle_dir = Path(args.bundle_dir).resolve()
    if not bundle_dir.is_dir():
        print(f"ERROR: Bundle directory does not exist: {bundle_dir}", file=sys.stderr)
        return 1

    pack_json_path = bundle_dir / "pack.json"
    existing: dict[str, Any] = {}
    if pack_json_path.exists():
        existing = json.loads(pack_json_path.read_text(encoding="utf-8"))

    existing_sprites: dict[str, dict[str, Any]] = {}
    for entry in existing.get("sprites", []):
        if "path" in entry:
            existing_sprites[entry["path"].replace("\\", "/")] = entry

    # Parse explicit --slice9 arguments
    slice_overrides: dict[str, str] = {}
    for item in args.slice9:
        if "=" not in item:
            print(f"ERROR: --slice9 expects 'rel/path.png=L,B,R,T' or 'rel/path.png=auto', got '{item}'", file=sys.stderr)
            return 2
        k, v = item.split("=", 1)
        slice_overrides[k.replace("\\", "/").strip()] = v.strip()

    # Skip workspace metadata folders if stored adjacent
    skip_dirs = {"_art_bible", "raw_gen", "qa", "anchors"}

    sprites_out: list[dict[str, Any]] = []
    pngs = sorted(bundle_dir.rglob("*.png"))

    for png in pngs:
        rel_parts = png.relative_to(bundle_dir).parts
        if any(p in skip_dirs for p in rel_parts):
            continue
        rel = norm_rel(bundle_dir, png)
        name = stem_name(bundle_dir, png)

        entry: dict[str, Any] = dict(existing_sprites.get(rel, {}))
        entry["path"] = rel
        entry["name"] = entry.get("name", name)

        # Wrap/tiling textures opt out of atlasing
        is_wrap = any(sub.replace("\\", "/") in rel for sub in args.wrap_glob)
        if is_wrap:
            entry["atlas"] = False
            entry["wrap"] = True

        # 9-slice borders
        if rel in slice_overrides:
            spec = slice_overrides[rel]
            if spec.lower() == "auto":
                borders = estimate_slice9(Image.open(png).convert("RGBA"))
            else:
                borders = [int(x.strip()) for x in spec.split(",")]
                if len(borders) != 4:
                    print(f"ERROR: Invalid slice9 spec for '{rel}': '{spec}'", file=sys.stderr)
                    return 2
            entry["slice"] = borders
            entry.setdefault("pivot", [0.5, 0.5])

        # Default pivots (preserve manual overrides)
        if "pivot" not in entry and not entry.get("wrap", False):
            if any(rel.startswith(pref.replace("\\", "/")) for pref in args.bottom_pivot_glob):
                entry["pivot"] = [0.5, 0.0]
            else:
                entry["pivot"] = [0.5, 0.5]

        sprites_out.append(entry)

    # Preserve or auto-discover fonts
    fonts_out: list[dict[str, Any]] = list(existing.get("fonts", []))
    known_fonts = {f["path"].replace("\\", "/") for f in fonts_out if "path" in f}
    for ttf in sorted(bundle_dir.rglob("*.ttf")):
        rel = norm_rel(bundle_dir, ttf)
        if rel not in known_fonts:
            fonts_out.append(
                {
                    "path": rel,
                    "name": ttf.stem,
                    "pixel_size": 64,
                    "atlas_size": 1024,
                    "sdf_padding": 8,
                }
            )

    manifest: dict[str, Any] = {
        "compression": existing.get("compression", args.compression),
        "atlas_compression": existing.get("atlas_compression", "auto"),
        "gltf_texture_compression": existing.get("gltf_texture_compression", "auto"),
        "max_atlas_size": existing.get("max_atlas_size", args.max_atlas),
        "atlas_padding": existing.get("atlas_padding", args.padding),
        "sprites": sprites_out,
    }
    if fonts_out:
        manifest["fonts"] = fonts_out

    pack_json_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {pack_json_path} ({len(sprites_out)} sprite(s), {len(fonts_out)} font(s))")

    if args.pack_out:
        if not args.bring_packer_proj:
            print("ERROR: --bring-packer-proj is required when --pack-out is set.", file=sys.stderr)
            return 2
        cmd = [
            "dotnet", "run", "--project", str(Path(args.bring_packer_proj).resolve()), "-c", "Release", "--",
            "--input", str(bundle_dir),
            "--output", str(Path(args.pack_out).resolve()),
            "--compression", args.compression,
            "--max-atlas", str(args.max_atlas),
            "--padding", str(args.padding),
        ]
        res = subprocess.run(cmd)
        return res.returncode

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
