#!/usr/bin/env python3
"""
provider_adapter.py — Optional pluggable external 2D image generation backend adapter.

By default, `bring-2d-art` uses Antigravity's built-in `image-generator` subagent (zero API keys,
zero extra dependencies). When a project's `art_bible.json` configures an external provider
(`recraft`, `fal`, or `scenario`) — globally or for a specific category like `ui_icon` — this
script calls that service cleanly using Python stdlib (`urllib.request`) and saves a lossless PNG.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


def http_json(url: str, headers: dict[str, str], payload: dict[str, Any] | None = None, method: str = "POST") -> dict[str, Any]:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {exc.code} from {url}: {body[:500]}") from exc


def download_bytes(url: str, headers: dict[str, str] | None = None) -> bytes:
    req = urllib.request.Request(url, headers=headers or {})
    with urllib.request.urlopen(req, timeout=120) as resp:
        return resp.read()


def gen_recraft(prompt: str, out_path: Path, cfg: dict[str, Any], width: int, height: int, vector: bool) -> None:
    api_key = os.environ.get("RECRAFT_API_KEY")
    if not api_key:
        raise RuntimeError("RECRAFT_API_KEY environment variable is not set.")

    payload: dict[str, Any] = {
        "prompt": prompt,
        "size": f"{width}x{height}",
        "response_format": "b64_json",
    }
    style_id = cfg.get("recraft_style_id")
    if style_id:
        payload["style_id"] = style_id
    else:
        payload["style"] = "vector_illustration" if vector else cfg.get("recraft_style", "digital_illustration")

    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
    }
    resp = http_json("https://external.api.recraft.ai/v1/images/generations", headers, payload)
    item = resp["data"][0]
    if "b64_json" in item:
        raw = base64.b64decode(item["b64_json"])
    elif "url" in item:
        raw = download_bytes(item["url"])
    else:
        raise RuntimeError(f"Unexpected Recraft response payload: {list(item.keys())}")

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(raw)


def gen_fal(prompt: str, out_path: Path, cfg: dict[str, Any], width: int, height: int, refs: list[Path]) -> None:
    api_key = os.environ.get("FAL_KEY")
    if not api_key:
        raise RuntimeError("FAL_KEY environment variable is not set.")

    model = cfg.get("fal_model", "fal-ai/flux-pro/v1.1")
    url = f"https://fal.run/{model}"
    payload: dict[str, Any] = {
        "prompt": prompt,
        "image_size": {"width": width, "height": height},
        "output_format": "png",
    }
    lora_url = cfg.get("fal_lora_url")
    if lora_url:
        payload["loras"] = [{"path": lora_url, "scale": 1.0}]
    if refs:
        # Encode first reference as data URI for models supporting image_url conditioning
        mime = "image/png" if refs[0].suffix.lower() == ".png" else "image/jpeg"
        b64 = base64.b64encode(refs[0].read_bytes()).decode("ascii")
        payload["image_url"] = f"data:{mime};base64,{b64}"

    headers = {
        "Authorization": f"Key {api_key}",
        "Content-Type": "application/json",
    }
    resp = http_json(url, headers, payload)
    images = resp.get("images") or []
    if not images:
        raise RuntimeError(f"Fal response contained no images: {resp}")
    raw = download_bytes(images[0]["url"])
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(raw)


def gen_scenario(prompt: str, out_path: Path, cfg: dict[str, Any], width: int, height: int) -> None:
    api_key = os.environ.get("SCENARIO_API_KEY")
    api_secret = os.environ.get("SCENARIO_API_SECRET")
    model_id = cfg.get("scenario_model_id")
    if not api_key or not api_secret:
        raise RuntimeError("SCENARIO_API_KEY and SCENARIO_API_SECRET environment variables must be set.")
    if not model_id:
        raise RuntimeError("art_bible.json backend.external_config.scenario_model_id must be set for Scenario.")

    token = base64.b64encode(f"{api_key}:{api_secret}".encode("utf-8")).decode("ascii")
    headers = {
        "Authorization": f"Basic {token}",
        "Content-Type": "application/json",
    }
    base_url = f"https://api.cloud.scenario.com/v1/models/{model_id}/inferences"
    create = http_json(
        base_url,
        headers,
        {"parameters": {"prompt": prompt, "numSamples": 1, "width": width, "height": height}},
    )
    inf_id = create["inference"]["id"]

    # Poll until succeeded
    for _ in range(60):
        status_resp = http_json(f"{base_url}/{inf_id}", headers, payload=None, method="GET")
        inf = status_resp["inference"]
        st = inf.get("status")
        if st == "succeeded":
            img_url = inf["images"][0]["url"]
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_bytes(download_bytes(img_url))
            return
        if st in ("failed", "canceled"):
            raise RuntimeError(f"Scenario inference {inf_id} ended with status '{st}'")
        time.sleep(2.0)
    raise RuntimeError(f"Timed out waiting for Scenario inference {inf_id}")


def cmd_check(_args: argparse.Namespace) -> int:
    statuses = {
        "builtin (image-generator)": "AVAILABLE (default, zero config)",
        "recraft": "READY (RECRAFT_API_KEY set)" if os.environ.get("RECRAFT_API_KEY") else "not configured (set RECRAFT_API_KEY)",
        "fal": "READY (FAL_KEY set)" if os.environ.get("FAL_KEY") else "not configured (set FAL_KEY)",
        "scenario": (
            "READY (SCENARIO_API_KEY + SCENARIO_API_SECRET set)"
            if (os.environ.get("SCENARIO_API_KEY") and os.environ.get("SCENARIO_API_SECRET"))
            else "not configured (set SCENARIO_API_KEY & SCENARIO_API_SECRET)"
        ),
    }
    print("2D Art Generation Providers:")
    for k, v in statuses.items():
        print(f"  {k:<28} : {v}")
    return 0


def cmd_generate(args: argparse.Namespace) -> int:
    bible = json.loads(Path(args.bible).resolve().read_text(encoding="utf-8"))
    backend = bible.get("backend", {})
    provider = args.provider or backend.get("provider", "builtin")
    cfg = backend.get("external_config", {})

    if provider == "builtin":
        print(
            "Provider is 'builtin': invoke the Antigravity built-in `image-generator` subagent directly "
            "using the compiled prompt and reference paths from `art_bible_tool.py prompt`."
        )
        return 0

    out_path = Path(args.out).resolve()
    refs = [Path(r).resolve() for r in (args.ref or [])]

    if provider == "recraft":
        gen_recraft(args.prompt, out_path, cfg, args.width, args.height, args.vector)
    elif provider == "fal":
        gen_fal(args.prompt, out_path, cfg, args.width, args.height, refs)
    elif provider == "scenario":
        gen_scenario(args.prompt, out_path, cfg, args.width, args.height)
    else:
        print(f"ERROR: Unsupported provider '{provider}'", file=sys.stderr)
        return 2

    print(f"Generated via {provider}: {out_path}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Optional external 2D generation provider adapter")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("check", help="Check which image generation backends are configured")

    p_gen = sub.add_parser("generate", help="Generate an image via configured external API provider")
    p_gen.add_argument("--bible", required=True)
    p_gen.add_argument("--provider", choices=["builtin", "recraft", "fal", "scenario"])
    p_gen.add_argument("--prompt", required=True)
    p_gen.add_argument("--out", required=True)
    p_gen.add_argument("--width", type=int, default=1024)
    p_gen.add_argument("--height", type=int, default=1024)
    p_gen.add_argument("--vector", action="store_true", help="Recraft SVG vector output")
    p_gen.add_argument("--ref", action="append", help="Optional reference image path(s)")

    args = parser.parse_args()
    if args.cmd == "check":
        return cmd_check(args)
    if args.cmd == "generate":
        return cmd_generate(args)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
