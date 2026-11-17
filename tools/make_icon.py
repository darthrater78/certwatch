#!/usr/bin/env python3
"""Draws the CertWatch icon (a shield holding a path that splits in two) and writes a
multi-size Windows .ico plus the source SVG. Pure standard library: no image packages.

Usage: python3 -I tools/make_icon.py <output-dir>
"""
import math
import struct
import sys
import zlib
from pathlib import Path

TILE = (0x1F, 0x2A, 0x44)
WHITE = (0xFF, 0xFF, 0xFF)
TEAL = (0x7F, 0xD3, 0xC0)
VIOLET = (0xB9, 0xA8, 0xFF)
# Line weight per output size (in the 64-unit design grid): heavier when small so it stays legible.
STROKE = {16: 6.0, 20: 5.5, 24: 5.5, 32: 5.0, 40: 4.5, 48: 4.5, 64: 4.0, 256: 4.0}
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]


def cubic(p0, p1, p2, p3, n=24):
    pts = []
    for i in range(1, n + 1):
        t = i / n
        mt = 1 - t
        pts.append((mt**3 * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t**3 * p3[0],
                    mt**3 * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t**3 * p3[1]))
    return pts


# Shield: M32 9 L50 16 V31 C50 43 42 51 32 55 C22 51 14 43 14 31 V16 Z
SHIELD = [(32, 9), (50, 16), (50, 31)] + cubic((50, 31), (50, 43), (42, 51), (32, 55)) \
    + cubic((32, 55), (22, 51), (14, 43), (14, 31)) + [(14, 16), (32, 9)]
SHIELD_SEGS = list(zip(SHIELD, SHIELD[1:]))
BRANCH_SEGS = [((32, 46), (32, 37)), ((32, 37), (24, 28)), ((32, 37), (40, 28))]
DOTS = [((23.5, 25.5), TEAL), ((40.5, 25.5), VIOLET)]


def seg_dist(px, py, a, b):
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def in_round_rect(x, y, r=14.0, size=64.0):
    cx = min(max(x, r), size - r)
    cy = min(max(y, r), size - r)
    return 0 <= x <= size and 0 <= y <= size and math.hypot(x - cx, y - cy) <= r


def sample(x, y, stroke):
    """Colour at one point of the 64-unit grid, or None outside the tile."""
    if not in_round_rect(x, y):
        return None
    for (c, colour) in DOTS:
        if math.hypot(x - c[0], y - c[1]) <= stroke:
            return colour
    half = stroke / 2
    if any(seg_dist(x, y, a, b) <= half for a, b in BRANCH_SEGS) or any(seg_dist(x, y, a, b) <= half for a, b in SHIELD_SEGS):
        return WHITE
    return TILE


def render(size, ss=4):
    stroke = STROKE[size]
    scale = 64.0 / size
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            r = g = b = a = 0
            for sy in range(ss):
                for sx in range(ss):
                    c = sample((px + (sx + 0.5) / ss) * scale, (py + (sy + 0.5) / ss) * scale, stroke)
                    if c is not None:
                        r += c[0]; g += c[1]; b += c[2]; a += 1
            n = ss * ss
            if a:
                row += bytes((r // a, g // a, b // a, round(255 * a / n)))
            else:
                row += b"\x00\x00\x00\x00"
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def ico(images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    return header + entries + blobs


SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="CertWatch">
  <rect width="64" height="64" rx="14" fill="#1F2A44"/>
  <path d="M32 9 L50 16 V31 C50 43 42 51 32 55 C22 51 14 43 14 31 V16 Z" fill="none" stroke="#FFFFFF" stroke-width="4" stroke-linejoin="round"/>
  <path d="M32 46 V37 M32 37 L24 28 M32 37 L40 28" fill="none" stroke="#FFFFFF" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="23.5" cy="25.5" r="4" fill="#7FD3C0"/>
  <circle cx="40.5" cy="25.5" r="4" fill="#B9A8FF"/>
</svg>
"""


def main():
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    images = [(s, png(s, render(s))) for s in SIZES]
    (out / "CertWatch.ico").write_bytes(ico(images))
    (out / "CertWatch.svg").write_text(SVG, encoding="utf-8")
    (out / "CertWatch-256.png").write_bytes(dict(images)[256])
    print("wrote", ", ".join(f"{s}px" for s in SIZES))


if __name__ == "__main__":
    main()
