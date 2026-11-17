#!/usr/bin/env python3
"""Regenerates src/CertWatch.App/Themes/{Flashbang,Dark}.xaml from the colour tokens in DESIGN.md.

Usage: python3 -I tools/gen_theme.py   (run from the repo root)
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOKENS = dict(re.findall(r'^\s{2}([a-z0-9-]+):\s*"(#[0-9A-Fa-f]{6})"',
                         (ROOT / "DESIGN.md").read_text(encoding="utf-8").split("---")[1], re.M))

# Brush key -> (Flashbang token, Dark token)
KEYS = {
    "Brand": ("brand", "brand-dark"), "OnBrand": ("on-brand", "on-brand"), "OnBrandMuted": ("on-brand-muted", "on-brand-muted"),
    "Ground": ("ground", "ground-dark"), "Surface": ("surface", "surface-dark"), "SurfaceAlt": ("surface-alt", "surface-alt-dark"),
    "Line": ("line", "line-dark"), "LineStrong": ("line-strong", "line-strong-dark"), "OnSurface": ("on-surface", "on-surface-dark"),
    "OnSurfaceMuted": ("on-surface-muted", "on-surface-muted-dark"), "Heading": ("heading", "heading-dark"),
    "PrimaryButton": ("brand", "heading-dark"), "OnPrimaryButton": ("on-brand", "brand-dark"),
    "Inspected": ("inspected", "inspected"), "InspectedTint": ("inspected-tint", "inspected-tint-dark"), "OnInspectedTint": ("on-inspected-tint", "on-inspected-tint-dark"),
    "Steered": ("steered", "steered"), "SteeredTint": ("steered-tint", "steered-tint-dark"), "OnSteeredTint": ("on-steered-tint", "on-steered-tint-dark"),
    "Direct": ("direct", "direct"), "DirectTint": ("direct-tint", "direct-tint-dark"), "OnDirectTint": ("on-direct-tint", "on-direct-tint-dark"),
    "Untrusted": ("untrusted", "untrusted"), "UntrustedTint": ("untrusted-tint", "untrusted-tint-dark"), "OnUntrustedTint": ("on-untrusted-tint", "on-untrusted-tint-dark"),
    "Neutral": ("on-surface-muted", "on-surface-muted-dark"), "NeutralTint": ("neutral-tint", "neutral-tint-dark"), "OnNeutralTint": ("on-neutral-tint", "on-neutral-tint-dark"),
    "OnStatus": ("on-status", "on-status"),
}

for theme, idx in (("Flashbang", 0), ("Dark", 1)):
    lines = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
             f'    <!-- {theme} theme. Generated from the colour tokens in DESIGN.md; edit the tokens, not this file. -->']
    lines += [f'    <SolidColorBrush x:Key="{k}" Color="{TOKENS[pair[idx]]}"/>' for k, pair in KEYS.items()]
    lines.append("</ResourceDictionary>")
    (ROOT / "src/CertWatch.App/Themes" / f"{theme}.xaml").write_text("\n".join(lines) + "\n", encoding="utf-8")
print("Themes regenerated")
