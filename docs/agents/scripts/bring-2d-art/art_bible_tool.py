#!/usr/bin/env python3
"""
art_bible_tool.py — Per-game 2D HD Art Bible initializer, validator, prompt compiler & palette swatch renderer.

Ensures interchangeable art styles across games while locking 100% consistent art direction
(palette, linework, brushwork, lighting, camera projection, contrast hierarchy, and visual anchors)
within a single game.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    Image = None  # type: ignore[assignment]


SCHEMA_VERSION = "1.0"

STYLE_PRESETS: dict[str, dict[str, Any]] = {
    "dark-fantasy-ink": {
        "display_name": "Dark Fantasy Hand-Inked Painterly",
        "inspirations": "Hades, Darkest Dungeon, Battle Chasers stylized HD 2D illustration",
        "style_summary": (
            "high-definition hand-drawn and hand-painted 2D dark-fantasy game art, "
            "crisp variable-weight dark ink contour lines, rich sculpted cel-painterly brushwork, "
            "dramatic specular rim highlights, strong silhouette readability"
        ),
        "linework": {
            "outline_color": "#141118",
            "weight_character": "medium-bold tapered dark ink (3-4px at 1024px)",
            "weight_prop": "clean medium dark ink (2-3px at 1024px)",
            "weight_background": "soft low-contrast atmospheric ink or painterly lineless edges",
            "interior_detail": "selective crisp ink accents with subtle hand-drawn hatching in deep creases",
        },
        "shading": (
            "sculpted painterly cel-shading with crisp shadow terminators, cool ambient bounce light, "
            "warm specular edge gleams on metal and armor, rich hand-painted brush texture inside forms"
        ),
        "lighting": {
            "direction": "top-left 45-degree studio key light with subtle cool rim light from top-right",
            "mood": "dramatic high-contrast heroic clarity",
            "background_contrast": "muted, lower saturation and softer value contrast (values 18%-55%) so foreground pops",
            "foreground_contrast": "crisp high-contrast focal lighting (values 10%-95%) with saturated accents",
        },
        "palette": {
            "bg_deep": "#1A1926",
            "bg_mid": "#2E2C42",
            "bg_light": "#4C4866",
            "surface_neutral": "#5B5F70",
            "surface_warm": "#8A6F56",
            "primary_accent": "#C8323C",
            "secondary_accent": "#D99B38",
            "magic_accent": "#46C7D9",
            "ink_outline": "#141118",
            "highlight_tint": "#FFF1D6",
            "shadow_tint": "#1B162B",
        },
        "default_chroma": "#00FF00",
        "forbidden": [
            "photorealistic 3D render",
            "CGI plastic shading",
            "pixel art",
            "blurry airbrush mush",
            "neon over-bloom",
            "noisy film grain",
            "sketchy unfinished rough pencil lines",
        ],
    },
    "gothic-hand-inked": {
        "display_name": "Gothic Hand-Inked Metroidvania",
        "inspirations": "Hollow Knight, Silksong, Ender Lilies atmospheric hand-inked 2D art",
        "style_summary": (
            "high-definition hand-inked 2D gothic indie game illustration, "
            "fluid expressive black ink contours, velvety painterly washes, "
            "refined melancholic atmosphere, razor-sharp iconic silhouettes with luminous accents"
        ),
        "linework": {
            "outline_color": "#0D0E14",
            "weight_character": "bold confident tapered black ink (3-4px at 1024px)",
            "weight_prop": "clean medium black ink (2-3px at 1024px)",
            "weight_background": "delicate faded charcoal linework blending into misty washes",
            "interior_detail": "minimalist clean ink folds and cracked stone line hints",
        },
        "shading": (
            "smooth velvety painterly gradients with restrained cel shadow planes, "
            "soft glowing bioluminescent highlights, clean uncluttered planes"
        ),
        "lighting": {
            "direction": "soft overhead diffused ambient light with gentle glowing focal rim accents",
            "mood": "mysterious, poetic, high silhouette readability",
            "background_contrast": "misty desaturated cool slate and indigo washes (values 12%-48%)",
            "foreground_contrast": "crisp ivory, deep obsidian ink, and luminous focal glow accents",
        },
        "palette": {
            "bg_deep": "#121621",
            "bg_mid": "#242D3F",
            "bg_light": "#44536E",
            "surface_neutral": "#6B788E",
            "surface_warm": "#8D857C",
            "primary_accent": "#E8ECF2",
            "secondary_accent": "#5BC8E8",
            "magic_accent": "#F0A848",
            "ink_outline": "#0D0E14",
            "highlight_tint": "#F4F8FF",
            "shadow_tint": "#0F131D",
        },
        "default_chroma": "#00FF00",
        "forbidden": [
            "photorealism",
            "3D render",
            "overly busy micro-textures",
            "rainbow oversaturation",
            "pixel art",
            "harsh comic halftone dots",
        ],
    },
    "storybook-gouache": {
        "display_name": "Storybook Painterly Gouache",
        "inspirations": "Rayman Legends, Ori, Child of Light lush hand-painted 2D art",
        "style_summary": (
            "high-definition hand-painted gouache and storybook 2D game illustration, "
            "expressive visible traditional brushwork, warm colored ink-wash outlines, "
            "luminous layered foliage and stonework, handcrafted fairy-tale warmth and polish"
        ),
        "linework": {
            "outline_color": "#2E201B",
            "weight_character": "warm dark-umber expressive hand-painted contour (2-3px at 1024px)",
            "weight_prop": "soft colored-ink contour darker than local material (2px at 1024px)",
            "weight_background": "painterly lineless edges with soft atmospheric perspective",
            "interior_detail": "painterly dry-brush strokes, wood grain, leaf veins, woven fabric texture",
        },
        "shading": (
            "rich opaque gouache blending with warm sunlit highlights, cool teal-violet shadows, "
            "gentle bounce light, tactile traditional canvas brushwork"
        ),
        "lighting": {
            "direction": "warm sunlit top-left 45-degree golden key light with cool sky-blue shadows",
            "mood": "inviting, vibrant, handcrafted storybook depth",
            "background_contrast": "soft atmospheric haze and harmonized mid-values (values 35%-75%)",
            "foreground_contrast": "rich saturated focal materials and crisp warm highlights",
        },
        "palette": {
            "bg_deep": "#274152",
            "bg_mid": "#4E787F",
            "bg_light": "#93B8A8",
            "surface_neutral": "#9E8E7A",
            "surface_warm": "#C78B52",
            "primary_accent": "#E35D3B",
            "secondary_accent": "#F2C14E",
            "magic_accent": "#6BE0B8",
            "ink_outline": "#2E201B",
            "highlight_tint": "#FFF6D9",
            "shadow_tint": "#2A3554",
        },
        "default_chroma": "#FF00FF",
        "forbidden": [
            "harsh pure black outlines",
            "photorealism",
            "3D plastic gloss",
            "pixel art",
            "flat sterile vector gradients",
            "muddy grey shading",
        ],
    },
    "comic-cel-action": {
        "display_name": "Crisp Hand-Drawn Comic Cel Action",
        "inspirations": "Streets of Rage 4, Banner Saga, modern indie hand-drawn action art",
        "style_summary": (
            "high-definition hand-drawn 2D comic-illustrated game art, "
            "confident dynamic variable-weight ink line art, crisp two-tone cel shading "
            "layered with subtle painterly surface texture, punchy readable action silhouettes"
        ),
        "linework": {
            "outline_color": "#11121A",
            "weight_character": "bold tapered comic ink outer silhouette (3-5px at 1024px), crisp 2px inner seams",
            "weight_prop": "clean uniform-tapered ink line (2-3px at 1024px)",
            "weight_background": "thin muted colored lines with simplified flat-painterly planes",
            "interior_detail": "crisp graphic shadow shapes and clean stylized material highlights",
        },
        "shading": (
            "hard-edged cel shadow planes with a subtle painterly gradient lift, "
            "sharp rim highlights, clean material read at fast 60 FPS gameplay speed"
        ),
        "lighting": {
            "direction": "strong top-left 45-degree directional key light with crisp opposite rim edge",
            "mood": "kinetic, clean, high-clarity modern arcade polish",
            "background_contrast": "simplified lower-contrast color blocks so action sprites pop cleanly",
            "foreground_contrast": "punchy bold silhouettes, crisp highlights, vibrant accents",
        },
        "palette": {
            "bg_deep": "#1D243B",
            "bg_mid": "#354469",
            "bg_light": "#6B7FA8",
            "surface_neutral": "#7D8596",
            "surface_warm": "#C98D59",
            "primary_accent": "#F04848",
            "secondary_accent": "#F7B538",
            "magic_accent": "#38D9C0",
            "ink_outline": "#11121A",
            "highlight_tint": "#FFF9E6",
            "shadow_tint": "#1D1E38",
        },
        "default_chroma": "#00FF00",
        "forbidden": [
            "photorealistic textures",
            "3D CGI",
            "fuzzy un-inked edges",
            "pixel art",
            "muddy brown noise",
        ],
    },
    "cozy-watercolor": {
        "display_name": "Cozy Hand-Painted Watercolor & Ink",
        "inspirations": "Spiritfarer, Gris, Studio Ghibli background watercolor illustration",
        "style_summary": (
            "high-definition hand-drawn cozy 2D watercolor and colored-ink game illustration, "
            "delicate expressive sepia-brown outlines, translucent layered watercolor washes, "
            "gentle sunlit warmth, charming handcrafted storybook clarity"
        ),
        "linework": {
            "outline_color": "#4A362C",
            "weight_character": "delicate clean warm-sepia hand-drawn ink (2-3px at 1024px)",
            "weight_prop": "fine warm-brown ink linework (2px at 1024px)",
            "weight_background": "ultra-soft watercolor edge blooms with minimal linework",
            "interior_detail": "subtle hand-drawn ink accents and soft watercolor pigment granulation",
        },
        "shading": (
            "soft translucent watercolor washes with warm golden sunlight, gentle lavender-blue shadows, "
            "clean readable forms without muddy over-blending"
        ),
        "lighting": {
            "direction": "soft warm afternoon sunlight from top-left with airy bounced fill light",
            "mood": "serene, cozy, welcoming, gentle harmony",
            "background_contrast": "airy pastel washes with low contrast (values 55%-92%)",
            "foreground_contrast": "clear warm mid-darks and crisp sepia linework for easy gameplay reading",
        },
        "palette": {
            "bg_deep": "#6C7E96",
            "bg_mid": "#A5B8C4",
            "bg_light": "#E8DFD1",
            "surface_neutral": "#C9BCA9",
            "surface_warm": "#D9A477",
            "primary_accent": "#D96B52",
            "secondary_accent": "#E8B954",
            "magic_accent": "#6DB8A6",
            "ink_outline": "#4A362C",
            "highlight_tint": "#FFFBF0",
            "shadow_tint": "#6B6782",
        },
        "default_chroma": "#FF00FF",
        "forbidden": [
            "harsh neon colors",
            "pure black heavy comic shadows",
            "3D CGI",
            "pixel art",
            "gritty grimdark grime",
        ],
    },
    "grim-woodcut-etching": {
        "display_name": "Grim Hand-Inked Woodcut & Hatching",
        "inspirations": "Darkest Dungeon, Don't Starve, medieval illuminated ink illustration",
        "style_summary": (
            "high-definition hand-drawn grim ink and crosshatched 2D game illustration, "
            "heavy dramatic black ink shadows, controlled hand-drawn engraving hatching, "
            "muted parchment and weathered ochre-crimson washes, theatrical gothic character"
        ),
        "linework": {
            "outline_color": "#0C0A09",
            "weight_character": "bold jagged expressive dip-pen ink (3-5px at 1024px)",
            "weight_prop": "strong hand-inked pen contour with shadow hatching (2-4px at 1024px)",
            "weight_background": "weathered charcoal etching lines blended into dark sepia wash",
            "interior_detail": "deliberate parallel crosshatching and heavy pooled black shadow blocks",
        },
        "shading": (
            "high-contrast chiaroscuro ink blocks paired with muted hand-tinted watercolor/parchment fills, "
            "dramatic theatrical rim highlights"
        ),
        "lighting": {
            "direction": "harsh top-left torchlight key casting crisp ink shadows",
            "mood": "tense, gothic, hand-inked tabletop storybook dread",
            "background_contrast": "dark brooding umber and slate tones (values 8%-42%)",
            "foreground_contrast": "bone parchment highlights, deep ink shadows, blood-crimson and tarnished-gold focal accents",
        },
        "palette": {
            "bg_deep": "#141110",
            "bg_mid": "#2B2420",
            "bg_light": "#52463D",
            "surface_neutral": "#75685C",
            "surface_warm": "#A68C69",
            "primary_accent": "#9E2424",
            "secondary_accent": "#C99A3B",
            "magic_accent": "#5A9E86",
            "ink_outline": "#0C0A09",
            "highlight_tint": "#E8DBC3",
            "shadow_tint": "#0C0A09",
        },
        "default_chroma": "#00FF00",
        "forbidden": [
            "glossy 3D render",
            "clean corporate vector art",
            "pastel anime sheen",
            "pixel art",
            "soft blurry airbrush",
        ],
    },
    "sci-fi-ligne-claire": {
        "display_name": "Sci-Fi Ligne Claire Hand-Drawn",
        "inspirations": "Moebius, Sable, Transistor, European hand-drawn graphic novel art",
        "style_summary": (
            "high-definition hand-drawn ligne-claire sci-fi 2D game illustration, "
            "razor-clean architectural ink linework, luminous flat-plus-painterly color washes, "
            "crisp geometric tech decals, bold retro-futurist color harmony"
        ),
        "linework": {
            "outline_color": "#1A1E29",
            "weight_character": "crisp consistent technical-pen ink contour (2-3px at 1024px)",
            "weight_prop": "precise clean architectural ink line (2px at 1024px)",
            "weight_background": "fine colored drafting ink lines fading with distance",
            "interior_detail": "clean panel seams, minimalist stippling, crisp glowing UI readouts",
        },
        "shading": (
            "clean luminous graphic color planes with subtle painterly gradient washes "
            "and sharp sunlight-versus-shadow color shifts"
        ),
        "lighting": {
            "direction": "crisp sunlit top-left key light with clean cool turquoise bounce shadows",
            "mood": "wondrous, sleek, graphic-novel sci-fi precision",
            "background_contrast": "atmospheric pastel desert/nebula washes (values 35%-78%)",
            "foreground_contrast": "crisp dark-navy ink, bright ceramic whites, coral and cyan accents",
        },
        "palette": {
            "bg_deep": "#1C2938",
            "bg_mid": "#3C5E73",
            "bg_light": "#84B5BF",
            "surface_neutral": "#D4D2C7",
            "surface_warm": "#DE9A6F",
            "primary_accent": "#EB5E4B",
            "secondary_accent": "#F2C45A",
            "magic_accent": "#3FE0D0",
            "ink_outline": "#1A1E29",
            "highlight_tint": "#FAF8F0",
            "shadow_tint": "#263B59",
        },
        "default_chroma": "#FF00FF",
        "forbidden": [
            "photorealistic Unreal Engine render",
            "grimy muddy rust overload",
            "pixel art",
            "messy sketchy scribbles",
        ],
    },
    "vibrant-arcade-illustrated": {
        "display_name": "Vibrant Illustrated Modern Arcade",
        "inspirations": "Castle Crashers HD, Wargroove portraits, vibrant hand-drawn indie arcade art",
        "style_summary": (
            "high-definition vibrant hand-drawn 2D arcade game illustration, "
            "bold smooth hand-inked outlines, lively saturated painterly cel colors, "
            "glossy readable materials, joyful crystal-clear silhouettes at 60 FPS"
        ),
        "linework": {
            "outline_color": "#1A1526",
            "weight_character": "bold smooth dark-plum ink outline (3-4px at 1024px)",
            "weight_prop": "clean expressive medium ink outline (2-3px at 1024px)",
            "weight_background": "soft harmonized colored outlines with clean painted shapes",
            "interior_detail": "clean stylized shine marks, crisp material folds, uncluttered forms",
        },
        "shading": (
            "bright punchy two-tone cel shading with soft warm rim light and glossy specular highlights"
        ),
        "lighting": {
            "direction": "bright cheerful top-left 45-degree studio lighting with soft warm rim",
            "mood": "energetic, crisp, immediately readable on any display",
            "background_contrast": "harmonized mid-light painted shapes with soft value separation",
            "foreground_contrast": "saturated focal hues, crisp highlights, unmistakable silhouettes",
        },
        "palette": {
            "bg_deep": "#2A3966",
            "bg_mid": "#4E6FA6",
            "bg_light": "#91BDE0",
            "surface_neutral": "#B8AE9C",
            "surface_warm": "#D99559",
            "primary_accent": "#F24B59",
            "secondary_accent": "#FFCA3A",
            "magic_accent": "#38D69B",
            "ink_outline": "#1A1526",
            "highlight_tint": "#FFFFFF",
            "shadow_tint": "#332B59",
        },
        "default_chroma": "#FF00FF",
        "forbidden": [
            "photorealism",
            "3D plastic clay",
            "desaturated muddy greys",
            "pixel art",
            "sketchy rough lines",
        ],
    },
}

PROJECTION_SPECS: dict[str, str] = {
    "side-view": (
        "strict 2D side-scroller orthographic view (0-degree flat side elevation, zero perspective vanishing point, "
        "characters facing right +X, platforms strictly horizontal)"
    ),
    "top-down-3q": (
        "classic 2D 3/4 top-down action RPG projection (approx 35-45 degree elevated orthographic view showing both "
        "top surfaces and front facades cleanly, zero lens distortion)"
    ),
    "top-down-flat": (
        "strict 90-degree overhead orthographic top-down view (flat plan view looking straight down, zero perspective)"
    ),
    "isometric": (
        "strict 2:1 dimetric isometric orthographic projection (parallel lines never converge, 26.565-degree base edges)"
    ),
    "ui-orthographic": (
        "strictly flat front-facing 2D orthographic UI view (zero perspective tilt, crisp symmetric alignment)"
    ),
}

UNIVERSAL_BANS = (
    "no text, no typography, no watermark, no signature, no logo, no UI frame, "
    "no split panels, no multiple views, no floor shadow, no cast drop shadow, "
    "no background gradient, no vignette, no ambient glow bleeding into the backdrop"
)


def parse_hex_rgb(hex_color: str) -> tuple[int, int, int]:
    h = hex_color.strip().lstrip("#")
    if len(h) != 6:
        raise ValueError(f"Expected 6-digit hex color, got '{hex_color}'")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


def chroma_phrase(hex_color: str) -> str:
    h = hex_color.strip().upper()
    if h == "#00FF00":
        return "pure solid neon green #00FF00"
    if h == "#FF00FF":
        return "pure solid bright magenta #FF00FF"
    if h == "#000000":
        return "pure solid flat black #000000"
    return f"pure solid flat {h}"


def cmd_list_presets(_args: argparse.Namespace) -> int:
    print("Available HD Hand-Drawn Style Presets:")
    print("-" * 72)
    for key, p in STYLE_PRESETS.items():
        print(f"  {key:<28} {p['display_name']}")
        print(f"    Inspirations : {p['inspirations']}")
        print(f"    Default Chroma: {p['default_chroma']} | Ink: {p['palette']['ink_outline']}")
    return 0


def cmd_init(args: argparse.Namespace) -> int:
    out_dir = Path(args.out_dir).resolve()
    for sub in ("anchors", "raw_gen", "prepared", "qa"):
        (out_dir / sub).mkdir(parents=True, exist_ok=True)

    preset_key = args.preset
    if preset_key not in STYLE_PRESETS:
        print(f"ERROR: Unknown preset '{preset_key}'. Run 'list-presets' to see options.", file=sys.stderr)
        return 2

    preset = json.loads(json.dumps(STYLE_PRESETS[preset_key]))

    if args.custom_style:
        preset["style_summary"] = args.custom_style
    if args.light_dir:
        preset["lighting"]["direction"] = args.light_dir
    if args.chroma:
        parse_hex_rgb(args.chroma)
        preset["default_chroma"] = args.chroma.upper()
    if args.palette_override:
        for pair in args.palette_override:
            if "=" not in pair:
                print(f"ERROR: --palette-override expects key=#RRGGBB, got '{pair}'", file=sys.stderr)
                return 2
            k, v = pair.split("=", 1)
            parse_hex_rgb(v)
            preset["palette"][k.strip()] = v.strip().upper()

    projection = args.projection
    if projection not in PROJECTION_SPECS:
        print(
            f"ERROR: Unknown projection '{projection}'. Choose from: {', '.join(PROJECTION_SPECS.keys())}",
            file=sys.stderr,
        )
        return 2

    bible: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "game_name": args.game,
        "slug": args.slug or args.game.lower().replace(" ", "-"),
        "preset": preset_key,
        "display_name": preset["display_name"],
        "theme_notes": args.theme or "",
        "projection": projection,
        "projection_spec": PROJECTION_SPECS[projection],
        "target_resolution": {
            "reference_height_px": args.ref_height,
            "character_height_px": args.char_height,
            "tile_size_px": args.tile_size,
            "icon_size_px": args.icon_size,
        },
        "style_summary": preset["style_summary"],
        "linework": preset["linework"],
        "shading": preset["shading"],
        "lighting": preset["lighting"],
        "palette": preset["palette"],
        "chroma_key": {
            "default": preset["default_chroma"],
            "alternate": "#FF00FF" if preset["default_chroma"] == "#00FF00" else "#00FF00",
        },
        "forbidden": preset["forbidden"],
        "anchors": {
            "locked": False,
            "palette_swatch": "qa/palette_swatch.png",
            "world": "anchors/style_anchor_world.png",
            "characters": "anchors/style_anchor_characters.png",
            "ui": "anchors/style_anchor_ui.png",
        },
        "backend": {
            "provider": args.provider,
            "category_overrides": {},
            "external_config": {
                "recraft_style": "digital_illustration",
                "recraft_style_id": "",
                "fal_model": "fal-ai/flux-pro/v1.1",
                "fal_lora_url": "",
                "scenario_model_id": "",
            },
        },
        "approved_assets": [],
    }

    bible_path = out_dir / "art_bible.json"
    bible_path.write_text(json.dumps(bible, indent=2) + "\n", encoding="utf-8")

    # Render palette swatch immediately if Pillow is available
    swatch_path = out_dir / bible["anchors"]["palette_swatch"]
    if Image is not None:
        render_palette_swatch(bible, swatch_path)

    print(f"Initialized Art Bible: {bible_path}")
    print(f"  Style     : {bible['preset']} ({bible['display_name']})")
    print(f"  Projection: {bible['projection']}")
    print(f"  Chroma    : {bible['chroma_key']['default']} (alt: {bible['chroma_key']['alternate']})")
    print(f"  Backend   : {bible['backend']['provider']}")
    if swatch_path.exists():
        print(f"  Swatch    : {swatch_path}")
    return 0


def render_palette_swatch(bible: dict[str, Any], out_path: Path) -> None:
    if Image is None:
        raise RuntimeError("Pillow is required to render palette swatches.")
    palette: dict[str, str] = bible["palette"]
    items = list(palette.items())
    cell_w = 140
    cell_h = 110
    header_h = 44
    cols = min(6, max(1, len(items)))
    rows = (len(items) + cols - 1) // cols
    img_w = cols * cell_w + 24
    img_h = header_h + rows * cell_h + 20

    img = Image.new("RGB", (img_w, img_h), (24, 24, 30))
    draw = ImageDraw.Draw(img)
    try:
        font_title = ImageFont.load_default(size=16)
        font_lbl = ImageFont.load_default(size=12)
    except TypeError:
        font_title = ImageFont.load_default()
        font_lbl = ImageFont.load_default()

    title = f"{bible.get('game_name', 'Game')} - Locked Palette ({bible.get('preset', 'custom')})"
    draw.text((14, 12), title, fill=(240, 240, 245), font=font_title)

    for idx, (name, hex_val) in enumerate(items):
        r = idx // cols
        c = idx % cols
        x0 = 12 + c * cell_w
        y0 = header_h + r * cell_h
        rgb = parse_hex_rgb(hex_val)
        draw.rectangle([x0 + 4, y0 + 4, x0 + cell_w - 6, y0 + 68], fill=rgb, outline=(210, 210, 220), width=1)
        draw.text((x0 + 6, y0 + 74), name[:18], fill=(225, 225, 232), font=font_lbl)
        draw.text((x0 + 6, y0 + 90), hex_val.upper(), fill=(165, 170, 185), font=font_lbl)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    img.save(out_path, "PNG")


def cmd_palette_swatch(args: argparse.Namespace) -> int:
    bible_path = Path(args.bible).resolve()
    bible = json.loads(bible_path.read_text(encoding="utf-8"))
    out_path = Path(args.out).resolve() if args.out else (bible_path.parent / bible["anchors"]["palette_swatch"])
    render_palette_swatch(bible, out_path)
    print(f"Wrote palette swatch: {out_path}")
    return 0


def cmd_validate(args: argparse.Namespace) -> int:
    bible_path = Path(args.bible).resolve()
    if not bible_path.exists():
        print(f"ERROR: Art Bible not found: {bible_path}", file=sys.stderr)
        return 1

    bible = json.loads(bible_path.read_text(encoding="utf-8"))
    errors: list[str] = []
    warnings: list[str] = []

    for req in ("game_name", "projection", "style_summary", "linework", "shading", "lighting", "palette", "chroma_key", "anchors"):
        if req not in bible:
            errors.append(f"Missing required field '{req}'")

    if "palette" in bible:
        for k, v in bible["palette"].items():
            try:
                parse_hex_rgb(v)
            except Exception as exc:
                errors.append(f"Invalid palette color '{k}': {exc}")

    if "chroma_key" in bible:
        for k in ("default", "alternate"):
            if k in bible["chroma_key"]:
                try:
                    parse_hex_rgb(bible["chroma_key"][k])
                except Exception as exc:
                    errors.append(f"Invalid chroma_key.{k}: {exc}")

    base = bible_path.parent
    anchors = bible.get("anchors", {})
    present_anchors = []
    for k in ("world", "characters", "ui"):
        rel = anchors.get(k)
        if rel and (base / rel).exists():
            present_anchors.append(k)
        else:
            warnings.append(f"Anchor '{k}' not generated yet ({rel})")

    if not anchors.get("locked", False):
        warnings.append("anchors.locked is false — generate and visually approve style anchors before batch asset generation")
    elif not present_anchors:
        errors.append("anchors.locked is true, but no anchor image files exist on disk!")

    for w in warnings:
        print(f"WARN : {w}")
    for e in errors:
        print(f"ERROR: {e}", file=sys.stderr)

    if errors:
        return 1
    print(f"OK: Art Bible valid ({bible_path.name}). Existing anchors: {', '.join(present_anchors) or 'none yet'}.")
    return 0


def cmd_lock(args: argparse.Namespace) -> int:
    bible_path = Path(args.bible).resolve()
    bible = json.loads(bible_path.read_text(encoding="utf-8"))
    base = bible_path.parent
    existing = [k for k in ("world", "characters", "ui") if (base / bible["anchors"][k]).exists()]
    if not existing:
        print("ERROR: Cannot lock Art Bible — no anchor images exist under anchors/ yet.", file=sys.stderr)
        return 1
    bible["anchors"]["locked"] = True
    bible_path.write_text(json.dumps(bible, indent=2) + "\n", encoding="utf-8")
    print(f"Locked Art Bible '{bible['game_name']}' with anchors: {', '.join(existing)}")
    return 0


def cmd_prompt(args: argparse.Namespace) -> int:
    bible_path = Path(args.bible).resolve()
    bible = json.loads(bible_path.read_text(encoding="utf-8"))
    base = bible_path.parent

    cat = args.category
    subject = args.subject
    chroma_hex = (args.chroma or bible["chroma_key"]["default"]).upper()
    chroma_desc = chroma_phrase(chroma_hex)

    pal = bible["palette"]
    palette_summary = (
        f"bg_deep {pal.get('bg_deep')}, bg_mid {pal.get('bg_mid')}, surface {pal.get('surface_neutral')}/"
        f"{pal.get('surface_warm')}, primary accent {pal.get('primary_accent')}, "
        f"secondary accent {pal.get('secondary_accent')}, magic/highlight accent {pal.get('magic_accent')}, "
        f"ink outline {pal.get('ink_outline')}, shadow tint {pal.get('shadow_tint')}"
    )

    lw = bible["linework"]
    light = bible["lighting"]
    forbidden = ", ".join(bible.get("forbidden", []))

    # Resolve visual anchor references to pass to image-generator
    refs: list[str] = []
    swatch = base / bible["anchors"]["palette_swatch"]
    world_anchor = base / bible["anchors"]["world"]
    char_anchor = base / bible["anchors"]["characters"]
    ui_anchor = base / bible["anchors"]["ui"]

    if cat == "anchor_world":
        if swatch.exists():
            refs.append(str(swatch))
    elif cat == "anchor_character":
        if world_anchor.exists():
            refs.append(str(world_anchor))
        if swatch.exists():
            refs.append(str(swatch))
    elif cat == "anchor_ui":
        if world_anchor.exists():
            refs.append(str(world_anchor))
        if swatch.exists():
            refs.append(str(swatch))
    elif cat in ("prop", "tile_seamless", "parallax_layer", "environment_piece"):
        if world_anchor.exists():
            refs.append(str(world_anchor))
        elif char_anchor.exists():
            refs.append(str(char_anchor))
        elif swatch.exists():
            refs.append(str(swatch))
    elif cat in ("character_static", "portrait", "vfx_frame", "rig_hero_anchor", "rig_modular_part"):
        if char_anchor.exists():
            refs.append(str(char_anchor))
        elif world_anchor.exists():
            refs.append(str(world_anchor))
        elif swatch.exists():
            refs.append(str(swatch))
    elif cat in ("ui_panel", "ui_button", "ui_icon"):
        if ui_anchor.exists():
            refs.append(str(ui_anchor))
        elif world_anchor.exists():
            refs.append(str(world_anchor))
        elif char_anchor.exists():
            refs.append(str(char_anchor))
        elif swatch.exists():
            refs.append(str(swatch))

    if args.extra_ref:
        for r in args.extra_ref:
            refs.append(str(Path(r).resolve()))

    # Category-specific rules
    if cat == "anchor_world":
        cat_block = (
            f"Create the master Visual Style Anchor showcase for the game '{bible['game_name']}': {subject}. "
            f"Render a single cohesive atmospheric environment vignette (landscape framing) establishing the game's "
            f"exact architectural stonework/foliage brushwork, surface materials, lighting mood ({light['mood']}), "
            f"color hierarchy ({light['background_contrast']}), and camera angle ({bible['projection_spec']}). "
            f"No text, no UI overlay, no split panels, no watermark."
        )
        line_spec = lw["weight_prop"]
        bg_clause = "Full-bleed cohesive painted game backdrop (no chroma background)."
    elif cat == "anchor_character":
        cat_block = (
            f"Matching the EXACT art style, palette, brushwork, and lighting of the reference world style anchor: "
            f"Create the master Character Style Anchor for '{bible['game_name']}': {subject}. "
            f"Full body head-to-toe in a clean iconic neutral combat-ready stance facing RIGHT (+X), limbs clearly separated "
            f"from torso for silhouette readability. {bible['projection_spec']}. "
            f"Foreground contrast rule: {light['foreground_contrast']}."
        )
        line_spec = lw["weight_character"]
        bg_clause = f"Centered with comfortable empty margin on all sides, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "anchor_ui":
        cat_block = (
            f"Matching the EXACT art style, palette, ink linework, and material craft of the reference style anchor: "
            f"Create the master UI Style Anchor panel for '{bible['game_name']}': {subject}. "
            f"Render ONE clean, symmetric horizontal rectangular UI frame/panel centered straight-on in strict 2D orthographic view, "
            f"ornate corners with clean straight stretchable middle edges (ready for 9-slice scaling), empty uniform dark interior. "
            f"Zero text, zero numbers, zero inner icons."
        )
        line_spec = lw["weight_prop"]
        bg_clause = f"Centered with generous margin, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "prop":
        cat_block = (
            f"Matching the EXACT art style, ink outline weight, brushwork, palette, and lighting direction of the reference anchor(s): "
            f"Render ONLY this single isolated 2D game prop: {subject}. "
            f"{bible['projection_spec']}. Crisp clean silhouette with grounded base and {light['foreground_contrast']}."
        )
        line_spec = lw["weight_prop"]
        bg_clause = f"Single centered object with generous empty margin, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "character_static":
        cat_block = (
            f"Matching the EXACT art style, proportions, ink outline weight, brushwork, palette, and lighting of the reference character anchor: "
            f"Render ONLY this single isolated 2D game character/creature sprite: {subject}. "
            f"{bible['projection_spec']}. Facing RIGHT (+X), crisp unmistakable silhouette, {light['foreground_contrast']}."
        )
        line_spec = lw["weight_character"]
        bg_clause = f"Single centered character head-to-toe with generous empty margin, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "tile_seamless":
        cat_block = (
            f"Matching the EXACT art style, brushwork, palette, and lighting of the reference world style anchor: "
            f"Render a full-bleed edge-to-edge SEAMLESSLY TILEABLE 2D game terrain/surface texture of: {subject}. "
            f"Strict flat orthographic view, even uniform lighting across the entire square with zero dark corners, "
            f"zero vignette, zero central landmark that creates obvious repetition, edges designed to wrap smoothly left-to-right and top-to-bottom. "
            f"Background contrast rule: {light['background_contrast']}."
        )
        line_spec = lw["weight_background"]
        bg_clause = "Full-bleed edge-to-edge square texture (no borders, no frames, no chroma backdrop, no text)."
    elif cat == "parallax_layer":
        cat_block = (
            f"Matching the EXACT art style, brushwork, palette, and atmosphere of the reference world style anchor: "
            f"Render a wide 2D horizontal side-scrolling parallax background band of: {subject}. "
            f"Designed to tile cleanly horizontally (left and right edges match height and horizon continuity), "
            f"strict side orthographic elevation, softer value contrast and lower saturation than foreground sprites ({light['background_contrast']})."
        )
        line_spec = lw["weight_background"]
        bg_clause = (
            f"If mid/near parallax silhouette band: isolated cleanly above a solid flat {chroma_desc} sky area for transparent layering "
            f"(or full-bleed painted sky if far skybox layer). No text, no characters, no foreground UI."
        )
    elif cat in ("ui_panel", "ui_button"):
        kind = "9-slice scalable UI window panel with empty interior" if cat == "ui_panel" else "9-slice scalable horizontal UI button frame with empty center"
        cat_block = (
            f"Matching the EXACT art style, border trim, ink weight, and material craft of the reference UI anchor: "
            f"Render ONLY ONE isolated symmetric {kind}: {subject}. "
            f"Strict front-facing 2D orthographic view, perfectly horizontal and vertical outer edges, crisp ornate corners "
            f"and clean straight stretchable middle edges, completely blank center (ZERO text, ZERO letters, ZERO icons inside)."
        )
        line_spec = lw["weight_prop"]
        bg_clause = f"Centered with generous margin, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "ui_icon":
        cat_block = (
            f"Matching the EXACT art style, ink outline weight, shading, and palette of the reference UI anchor: "
            f"Render ONLY ONE isolated 2D game HUD/inventory icon of: {subject}. "
            f"Bold clear iconic silhouette readable down to 48x48 pixels, centered square composition, no outer box/frame unless requested."
        )
        line_spec = lw["weight_character"]
        bg_clause = f"Centered with comfortable margin, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "vfx_frame":
        cat_block = (
            f"Matching the EXACT hand-drawn painterly brushwork and accent palette of the reference anchor: "
            f"Render ONLY ONE isolated 2D hand-drawn gameplay VFX element: {subject}. "
            f"Bold dynamic painted energy strokes and crisp glowing shapes, centered cleanly."
        )
        line_spec = "stylized painterly glow edges with optional sharp ink sparks"
        bg_clause = f"Centered with generous margin, isolated on a completely flat {chroma_desc} background (zero haze bleeding to frame edges). {UNIVERSAL_BANS}."
    elif cat == "portrait":
        cat_block = (
            f"Matching the EXACT art style, facial styling, ink linework, brushwork, and palette of the reference character anchor: "
            f"Render a high-definition 2D character dialogue/HUD bust portrait (head and shoulders) of: {subject}. "
            f"Expressive crisp silhouette, dramatic key lighting matching the game."
        )
        line_spec = lw["weight_character"]
        bg_clause = f"Clean bust composition isolated on a completely flat {chroma_desc} background (or simple painted vignette if framed). No text, no speech bubble."
    elif cat == "rig_hero_anchor":
        cat_block = (
            f"Matching the EXACT art style, ink outline weight, brushwork, palette, and lighting of the reference style anchor: "
            f"Render the full-body Rig2D Hero Concept Anchor for skeletal 2D animation of: {subject}. "
            f"Full body head-to-toe in a clean 3/4 side-facing neutral combat-ready stance facing RIGHT (+X), "
            f"arms, weapon, and legs clearly separated from the torso so every limb silhouette is distinct and easy to decompose into modular rig parts. "
            f"Foreground contrast rule: {light['foreground_contrast']}."
        )
        line_spec = lw["weight_character"]
        bg_clause = f"Single character centered with comfortable empty margin on all sides, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    elif cat == "rig_modular_part":
        cat_block = (
            f"Matching the EXACT art style, color palette, painterly brushwork, ink outline weight, and lighting of the reference character hero anchor: "
            f"Render ONLY the single isolated modular 2D skeletal rig part: {subject}. "
            f"Strict vertical upright orientation (top of part at top, bottom at bottom, facing RIGHT matching hero_anchor.png). "
            f"Include a clean, rounded, slightly extended overlap joint cap at its parent connection joint so rotating +/-90 degrees never exposes transparent seams or gaps. "
            f"Show ONLY this single part - omit all other body parts, head, torso, other limbs, and props."
        )
        line_spec = lw["weight_character"]
        bg_clause = f"Single centered modular part with generous empty margin, isolated on a completely flat {chroma_desc} background. {UNIVERSAL_BANS}."
    else:
        cat_block = f"Matching the EXACT art style of the reference anchor(s): Render {subject}."
        line_spec = lw["weight_prop"]
        bg_clause = f"Isolated on flat {chroma_desc} background. {UNIVERSAL_BANS}."

    prompt = (
        f"[STYLE LOCK — {bible['game_name']} / {bible['preset']}]\n"
        f"{bible['style_summary']}.\n"
        f"Linework: {line_spec}; outline color {lw['outline_color']}; interior detail: {lw['interior_detail']}.\n"
        f"Shading: {bible['shading']}.\n"
        f"Lighting: {light['direction']}.\n"
        f"Locked Color Palette: {palette_summary}.\n"
        f"\n"
        f"[ASSET DIRECTIVE — {cat}]\n"
        f"{cat_block}\n"
        f"{bg_clause}\n"
        f"Avoid: {forbidden}."
    )

    backend = bible.get("backend", {})
    provider = backend.get("category_overrides", {}).get(cat, backend.get("provider", "builtin"))

    out = {
        "provider": provider,
        "category": cat,
        "chroma": chroma_hex,
        "references": refs,
        "prompt": prompt,
    }

    if args.json:
        print(json.dumps(out, indent=2))
    else:
        print(f"=== PROVIDER: {provider} | CATEGORY: {cat} | CHROMA: {chroma_hex} ===")
        print("REFERENCES TO PASS:")
        if refs:
            for r in refs:
                print(f"  - {r}")
        else:
            print("  (none — this is the first root anchor)")
        print("\nPROMPT:")
        print(prompt)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Bring 2D Art Bible CLI")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("list-presets", help="List built-in HD hand-drawn style presets")

    p_init = sub.add_parser("init", help="Initialize art_bible.json and folders for a game")
    p_init.add_argument("--game", required=True, help="Game display name")
    p_init.add_argument("--slug", help="Optional kebab-case slug")
    p_init.add_argument("--preset", default="dark-fantasy-ink", help="Style preset key")
    p_init.add_argument("--projection", default="side-view", choices=list(PROJECTION_SPECS.keys()))
    p_init.add_argument("--theme", default="", help="Brief theme/world flavor notes")
    p_init.add_argument("--custom-style", help="Override style_summary with custom style prose")
    p_init.add_argument("--light-dir", help="Override lighting direction")
    p_init.add_argument("--chroma", help="Override default chroma hex (#00FF00 or #FF00FF)")
    p_init.add_argument("--palette-override", action="append", help="Override palette entry key=#RRGGBB")
    p_init.add_argument("--provider", default="builtin", choices=["builtin", "recraft", "fal", "scenario"])
    p_init.add_argument("--ref-height", type=int, default=1080)
    p_init.add_argument("--char-height", type=int, default=256)
    p_init.add_argument("--tile-size", type=int, default=256)
    p_init.add_argument("--icon-size", type=int, default=128)
    p_init.add_argument("--out-dir", required=True, help="Output art workspace directory (e.g. raw_assets/_art_bible or D:\\personal\\assets\\<slug>)")

    p_sw = sub.add_parser("palette-swatch", help="Render qa/palette_swatch.png from art_bible.json")
    p_sw.add_argument("--bible", required=True)
    p_sw.add_argument("--out")

    p_val = sub.add_parser("validate", help="Validate art_bible.json and anchor status")
    p_val.add_argument("--bible", required=True)

    p_lock = sub.add_parser("lock", help="Mark art_bible.json anchors as locked after visual approval")
    p_lock.add_argument("--bible", required=True)

    p_pr = sub.add_parser("prompt", help="Compile a locked style-consistent prompt + reference list")
    p_pr.add_argument("--bible", required=True)
    p_pr.add_argument(
        "--category",
        required=True,
        choices=[
            "anchor_world",
            "anchor_character",
            "anchor_ui",
            "prop",
            "character_static",
            "tile_seamless",
            "parallax_layer",
            "ui_panel",
            "ui_button",
            "ui_icon",
            "vfx_frame",
            "portrait",
            "rig_hero_anchor",
            "rig_modular_part",
        ],
    )
    p_pr.add_argument("--subject", required=True, help="Concrete description of the asset to generate")
    p_pr.add_argument("--chroma", help="Override chroma hex for this specific asset if palette clashes")
    p_pr.add_argument("--extra-ref", action="append", help="Additional category anchor image path(s)")
    p_pr.add_argument("--json", action="store_true", help="Output structured JSON")

    args = parser.parse_args()
    if args.cmd == "list-presets":
        return cmd_list_presets(args)
    if args.cmd == "init":
        return cmd_init(args)
    if args.cmd == "palette-swatch":
        return cmd_palette_swatch(args)
    if args.cmd == "validate":
        return cmd_validate(args)
    if args.cmd == "lock":
        return cmd_lock(args)
    if args.cmd == "prompt":
        return cmd_prompt(args)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
