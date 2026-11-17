#!/usr/bin/env python3
"""Renders the README mock-ups in docs/screenshots/ with a headless Chromium.

They are illustrations, not captures: the layout follows DESIGN.md and the app's XAML, colours come
from the DESIGN.md tokens (the same ones tools/gen_theme.py writes into the themes), the version from
the csproj, and every site, CA and adapter name is invented (Contoso/Fabrikam/Adatum, "ZTNA Adapter").
WPF can't run on Linux, so this is how the README stays current without a Windows machine.

Usage (from the repo root):
    CHROME=/path/to/chrome-headless-shell python3 -I tools/gen_mockups.py [name ...]
Names: dark-steered flashbang-inspected history picker log (default: all). popup.png isn't drawn here.
CHROME defaults to the newest Playwright headless shell under ~/.cache/ms-playwright.
"""
import html
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "screenshots"
SCALE = 2  # rendered at 2x, like a 200% Windows display

TOKENS = dict(re.findall(r'^\s{2}([a-z0-9-]+):\s*"(#[0-9A-Fa-f]{6})"',
                         (ROOT / "DESIGN.md").read_text(encoding="utf-8").split("---")[1], re.M))
VERSION = re.search(r"<Version>(.*?)</Version>",
                    (ROOT / "src/CertWatch.App/CertWatch.App.csproj").read_text(encoding="utf-8")).group(1)
ICON = (ROOT / "src/CertWatch.App/Assets/CertWatch-256.png").as_uri()

# Same brush-to-token mapping as tools/gen_theme.py.
BRUSHES = {
    "brand": ("brand", "brand-dark"), "on-brand": ("on-brand", "on-brand"), "on-brand-muted": ("on-brand-muted", "on-brand-muted-dark"),
    "ground": ("ground", "ground-dark"), "surface": ("surface", "surface-dark"), "surface-alt": ("surface-alt", "surface-alt-dark"),
    "line": ("line", "line-dark"), "line-strong": ("line-strong", "line-strong-dark"), "on-surface": ("on-surface", "on-surface-dark"),
    "muted": ("on-surface-muted", "on-surface-muted-dark"), "heading": ("heading", "heading-dark"),
    "primary": ("brand", "heading-dark"), "on-primary": ("on-brand", "brand-dark"),
    "inspected": ("inspected", "inspected-dark"), "inspected-tint": ("inspected-tint", "inspected-tint-dark"), "on-inspected-tint": ("on-inspected-tint", "on-inspected-tint-dark"),
    "steered": ("steered", "steered-dark"), "steered-tint": ("steered-tint", "steered-tint-dark"), "on-steered-tint": ("on-steered-tint", "on-steered-tint-dark"),
    "direct": ("direct", "direct-dark"), "direct-tint": ("direct-tint", "direct-tint-dark"), "on-direct-tint": ("on-direct-tint", "on-direct-tint-dark"),
    "neutral": ("on-surface-muted", "on-surface-muted-dark"), "neutral-tint": ("neutral-tint", "neutral-tint-dark"), "on-neutral-tint": ("on-neutral-tint", "on-neutral-tint-dark"),
    "selected-row": ("inspected-tint", "selected-row-dark"),
}

ICONS = {  # 24-unit stroke icons from Themes/Styles.xaml
    "inspected": "M5,11 H15 V19 H5 Z M7.5,11 V8.5 A2.5,2.5 0 0 1 12.5,8.5 V11 M17,4 A3,3 0 1 1 16.99,4 M19.2,9.2 L22,12",
    "steered": "M3,12 H7 C9,12 10,8 12,8 H21 M17,4 L21,8 L17,12",
    "direct": "M3,12 H21 M15,6 L21,12 L15,18",
    "browser": "M12,3 A9,9 0 1 1 11.99,3 M3,12 H21 M12,3 C15,6 15,18 12,21 M12,3 C9,6 9,18 12,21",
    "site": "M4,5 H20 V19 H4 Z M4,9 H20",
}
CHIP = {"inspected": "ZTNA · inspected", "steered": "ZTNA · steered", "direct": "Direct"}
e = html.escape


def css_vars(dark: bool) -> str:
    return ";".join(f"--{k}:{TOKENS[pair[1 if dark else 0]]}" for k, pair in BRUSHES.items())


def icon(name: str, size: int = 14, stroke: str = "currentColor", width: float = 2) -> str:
    return (f'<svg width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{stroke}" '
            f'stroke-width="{width}" stroke-linecap="round" stroke-linejoin="round"><path d="{ICONS[name]}"/></svg>')


def chip(status: str) -> str:
    return f'<span class="chip {status}">{icon(status, 12)}{CHIP[status]}</span>'


BASE_CSS = """
*{box-sizing:border-box;margin:0;padding:0}
body{font:13px/1.35 "Liberation Sans",Arial,sans-serif;color:var(--on-surface);background:var(--ground);overflow:hidden}
.mono{font-family:"DejaVu Sans Mono",monospace;font-size:12px}
.win{position:absolute;inset:0;display:flex;flex-direction:column;border:1px solid var(--line-strong)}
.titlebar{height:30px;display:flex;align-items:center;padding:0 10px;gap:8px;background:var(--ground);color:var(--muted);flex:none}
.titlebar .ctl{margin-left:auto;display:flex;gap:30px;padding-right:12px;color:var(--on-surface);font-size:14px}
.header{height:36px;background:var(--brand);color:var(--on-brand);display:flex;align-items:center;padding-left:10px;flex:none}
.header img{width:20px;height:20px}
.header .name{font-weight:600;margin-left:8px}
.header .ver{color:var(--on-brand-muted);font-size:12px;margin-left:8px}
.header a{color:var(--on-brand);font-size:12px;margin-left:10px;text-decoration:underline}
.header .right{margin-left:auto;display:flex}
.header .hb{padding:0 15px;line-height:36px}
.tabs{display:flex;padding:6px 0 0 10px;border-bottom:1px solid var(--line);flex:none}
.tab{padding:4px 14px 5px 12px;display:flex;align-items:center;gap:8px;border:1px solid transparent;border-bottom:none;margin-bottom:-1px}
.tab.sel{background:var(--surface);border-color:var(--line);border-radius:4px 4px 0 0}
.tab b{font-weight:600;display:block}
.tab small{display:block;color:var(--muted);font-size:11px}
.dot{width:8px;height:8px;border-radius:50%;flex:none}
.content{flex:1;background:var(--surface);padding:10px 12px 12px;display:flex;flex-direction:column;gap:10px;min-height:0}
.card{border:1px solid var(--line);border-radius:4px;flex:none}
.vhead{display:flex;align-items:center;gap:12px;padding:12px 12px 10px;border-radius:4px 4px 0 0}
.tile{width:40px;height:40px;border-radius:4px;display:flex;align-items:center;justify-content:center;flex:none}
.ctx{font-size:11px}
.headline{font-size:20px;font-weight:600;line-height:1.25}
.sub{font-size:11px}
.actions{margin-left:auto;display:flex;gap:10px}
.btn{height:32px;padding:0 12px;border:1px solid var(--line-strong);border-radius:4px;background:var(--surface-alt);color:var(--on-surface);display:inline-flex;align-items:center;white-space:nowrap}
.btn.small{height:28px;padding:0 10px}
.btn.primary{background:var(--primary);border-color:var(--primary);color:var(--on-primary);font-weight:600}
.path{display:flex;align-items:center;padding:10px 12px 4px}
.node{display:flex;align-items:center;gap:8px;border:1px solid var(--line-strong);border-radius:4px;padding:4px 10px 4px 5px}
.node .ni{width:24px;height:24px;border-radius:4px;display:flex;align-items:center;justify-content:center;background:var(--neutral-tint);color:var(--on-surface)}
.node b{display:block;font-weight:600}
.node small{display:block;font-size:11px;color:var(--muted)}
.arrow{width:36px;height:2px;margin:0 6px;position:relative}
.arrow:after{content:"";position:absolute;right:-1px;top:-4px;border-left:7px solid currentColor;border-top:5px solid transparent;border-bottom:5px solid transparent}
.facts{display:grid;grid-template-columns:repeat(4,1fr);gap:4px 12px;padding:8px 12px 10px}
.facts label{display:block;font-size:11px;color:var(--muted)}
.section{display:flex;align-items:center;gap:8px}
.section h3{font-size:14px;font-weight:600;color:var(--heading)}
.section .count{color:var(--muted)}
.section .tools{margin-left:auto;display:flex;align-items:center;gap:8px}
.input{width:170px;height:28px;border:1px solid var(--line-strong);border-radius:4px;background:var(--surface)}
table{width:100%;border-collapse:collapse;border:1px solid var(--line)}
th{background:var(--surface-alt);color:var(--muted);font-size:12px;font-weight:400;text-align:left;padding:0 10px;height:28px}
td{height:30px;padding:0 10px;border-top:1px solid var(--line);white-space:nowrap}
tr.sel td{background:var(--selected-row)}
td.site{font-weight:600}
.chip{display:inline-flex;align-items:center;gap:4px;border-radius:2px;padding:1px 6px 1px 5px;font-size:11px;font-weight:600}
.chip.inspected{background:var(--inspected-tint);color:var(--on-inspected-tint)}
.chip.steered{background:var(--steered-tint);color:var(--on-steered-tint)}
.chip.direct{background:var(--direct-tint);color:var(--on-direct-tint)}
.tablewrap{flex:1;min-height:0;border:1px solid var(--line);overflow:hidden}
.tablewrap table{border:none}
"""

TABS = [  # (name, status line, dot status)
    ("Edge", "crm.fabrikam.com · ZTNA · steered", "steered"),
    ("Brave", "news.adatum.com · ZTNA · inspected", "inspected"),
    ("Chrome", "www.wikipedia.org · Direct", "direct"),
]

SCENES = {
    "dark-steered": dict(
        dark=True, tab="Edge", status="steered", ctx="crm.fabrikam.com · Edge · checked 8:01:29 PM · own connection",
        headline="Steered to ZTNA · not inspected", sub="Chain ends at a public root: this is the site's own certificate",
        actions=["Re-check", "Copy SHA-256", "Export root"],
        path=[("browser", "Edge", "your browser"), ("steered", "ZTNA Adapter", "tunnel · 100.64.0.0/10"), ("site", "crm.fabrikam.com", "serves R11 · public")],
        facts=[("Issuer", "R11"), ("Root", "ISRG Root X1 · public"), ("Valid until", "Jan 8, 2027 (52 days)"), ("Protocol", "Tls13 · TLS_AES_256_GCM_SHA384"),
               ("Egress", "ZTNA Adapter"), ("Route", "100.64.0.0/10 → via 172.16.50.2", True), ("Connected to", "100.64.0.56", True), ("SHA-256", "4C1E07A2…9B3F1D", True)],
        sites="fabrikam", sel_site=0, history="fabrikam", sel_hist=None),
    "flashbang-inspected": dict(
        dark=False, tab="Brave", status="inspected", ctx="news.adatum.com · Brave · checked 8:02:10 PM · own connection",
        headline="Steered to ZTNA · inspected", sub="Chain ends at Contoso Root CA, a private root this PC trusts: a proxy replaced the certificate",
        actions=["Re-check", "Copy SHA-256", "Export root"],
        path=[("browser", "Brave", "your browser"), ("steered", "ZTNA Adapter", "tunnel · 100.64.0.0/10"),
              ("inspected", "Inspection", "certificate replaced"), ("site", "news.adatum.com", "original certificate hidden")],
        facts=[("Issuer", "Contoso Inspection CA"), ("Root", "Contoso Root CA · private"), ("Valid until", "Nov 23, 2026 (7 days)"), ("Protocol", "Tls13 · TLS_AES_128_GCM_SHA256"),
               ("Egress", "ZTNA Adapter"), ("Route", "100.64.0.0/10 → via 172.16.50.2", True), ("Connected to", "100.64.0.51", True), ("SHA-256", "B07D33E9…12C8A4", True)],
        sites="adatum", sel_site=0, history="adatum", sel_hist=None),
    "history": dict(
        dark=True, tab="Edge", status="direct", ctx="crm.fabrikam.com · Edge · saved Nov 16 20:00:12",
        headline="Direct · not inspected", sub="Chain ends at a public root: this is the site's own certificate",
        actions=["*Back to live", "Copy SHA-256", "Export root"],
        path=[("browser", "Edge", "your browser"), ("direct", "Wi-Fi", "direct · 0.0.0.0/0"), ("site", "crm.fabrikam.com", "serves R11 · public")],
        facts=[("Issuer", "R11"), ("Root", "ISRG Root X1 · public"), ("Valid until", "Jan 8, 2027 (52 days)"), ("Protocol", "Tls13"),
               ("Egress", "Wi-Fi"), ("Route", "0.0.0.0/0 → via 10.0.0.1", True), ("Connected to", "203.0.113.24", True), ("SHA-256", "4C1E07A2…9B3F1D", True)],
        sites="fabrikam", sel_site=0, history="fabrikam", sel_hist=1),
}

SITES = {
    "fabrikam": [("crm.fabrikam.com", "steered", "R11", "ZTNA Adapter", "Never inspected", "1", "Nov 16 20:01:29", ""),
                 ("files.fabrikam.com", "steered", "Sectigo RSA Domain Validation", "ZTNA Adapter", "Never inspected", "0", "Nov 16 19:58:02", "mTLS"),
                 ("www.wikipedia.org", "direct", "E6", "Wi-Fi", "Never inspected", "0", "Nov 16 19:52:44", "")],
    "adatum": [("news.adatum.com", "inspected", "Contoso Inspection CA", "ZTNA Adapter", "!Inspected now", "2", "Nov 16 20:02:10", ""),
               ("crm.fabrikam.com", "steered", "R11", "ZTNA Adapter", "Never inspected", "0", "Nov 16 20:01:51", ""),
               ("www.wikipedia.org", "direct", "E6", "Wi-Fi", "Never inspected", "0", "Nov 16 19:55:20", "")],
}
HISTORY = {
    "fabrikam": ("crm.fabrikam.com", [("Nov 16 20:00:44", "steered", "Egress interface changed", "ZTNA Adapter"),
                                      ("Nov 16 20:00:12", "direct", "First visit", "Wi-Fi")]),
    "adatum": ("news.adatum.com", [("Nov 16 19:48:56", "inspected", "Inspection started", "ZTNA Adapter"),
                                   ("Nov 16 19:47:56", "direct", "Inspection stopped", "Wi-Fi"),
                                   ("Nov 16 19:47:25", "inspected", "First visit", "ZTNA Adapter")]),
}


def titlebar(title: str, close_only: bool = False) -> str:
    controls = "<span>✕</span>" if close_only else "<span>—</span><span>☐</span><span>✕</span>"
    return (f'<div class="titlebar">{icon("steered", 14, "var(--on-surface)")}<span>{e(title)}</span>'
            f'<span class="ctl">{controls}</span></div>')


def header(dark: bool) -> str:
    theme = "Dark" if dark else "Flashbang"
    buttons = ["Browsers…", "Clear all…", "Log", "Pop-ups: until dismissed", f"Theme: {theme}"]
    return (f'<div class="header"><img src="{ICON}"><span class="name">CertWatch</span><span class="ver">v{e(VERSION)}</span>'
            '<a>GitHub</a><a>Release notes</a><span class="right">'
            + "".join(f'<span class="hb">{e(b)}</span>' for b in buttons) + "</span></div>")


def tabs(selected: str, extra=()) -> str:
    out = []
    for name, line, status in [*TABS, *extra]:
        dot = f"var(--{status})" if status != "neutral" else "var(--neutral)"
        out.append(f'<div class="tab{" sel" if name == selected else ""}"><span class="dot" style="background:{dot}"></span>'
                   f'<span><b>{e(name)}</b><small>{e(line)}</small></span></div>')
    return f'<div class="tabs">{"".join(out)}</div>'


def card(s: dict) -> str:
    st = s["status"]
    acts = "".join(f'<span class="btn primary">{e(a[1:])}</span>' if a.startswith("*") else f'<span class="btn">{e(a)}</span>'
                   for a in s["actions"])
    nodes = []
    for i, (ic, title, sub) in enumerate(s["path"]):
        if i:
            nodes.append(f'<span class="arrow" style="background:var(--{st});color:var(--{st})"></span>')
        tint = f"background:var(--{ic}-tint);color:var(--on-{ic}-tint)" if ic in CHIP else ""
        nodes.append(f'<span class="node"><span class="ni" style="{tint}">{icon(ic, 16)}</span><span><b>{e(title)}</b><small>{e(sub)}</small></span></span>')
    facts = "".join(f'<div><label>{e(f[0])}</label><div class="{"mono" if len(f) > 2 else ""}">{e(f[1])}</div></div>' for f in s["facts"])
    return (f'<div class="card"><div class="vhead" style="background:var(--{st}-tint);color:var(--on-{st}-tint)">'
            f'<span class="tile" style="background:var(--{st})">{icon(st, 24, "#fff", 2)}</span>'
            f'<span><div class="ctx">{e(s["ctx"])}</div><div class="headline">{e(s["headline"])}</div><div class="sub">{e(s["sub"])}</div></span>'
            f'<span class="actions">{acts}</span></div><div class="path">{"".join(nodes)}</div><div class="facts">{facts}</div></div>')


def sites_table(key: str, sel: int | None, browser: str) -> str:
    rows = []
    for i, (site, st, issuer, egress, insp, changes, seen, note) in enumerate(SITES[key]):
        insp_html = (f'<b style="color:var(--inspected)">{e(insp[1:])}</b>' if insp.startswith("!")
                     else f'<span style="color:var(--muted)">{e(insp)}</span>')
        rows.append(f'<tr class="{"sel" if i == sel else ""}"><td class="site">{e(site)}</td><td>{chip(st)}</td><td>{e(issuer)}</td>'
                    f'<td>{e(egress)}</td><td>{insp_html}</td><td>{changes}</td><td>{e(seen)}</td><td>{e(note)}</td></tr>')
    return (f'<div class="section"><h3>Sites</h3><span class="count">{len(SITES[key])} seen</span><span class="tools">'
            '<span style="color:var(--muted)">Filter</span><span class="input"></span><span class="btn small">Clear site</span>'
            '<span class="btn small">Clear browser…</span></span></div>'
            '<div class="tablewrap" style="flex:3"><table><tr><th>Site</th><th>Now</th><th>Issuer</th><th>Egress</th><th>Inspection</th>'
            f'<th>Changes</th><th>Last seen</th><th></th></tr>{"".join(rows)}</table></div>')


def history_table(key: str, sel: int | None) -> str:
    site, entries = HISTORY[key]
    rows = "".join(f'<tr class="{"sel" if i == sel else ""}"><td>{e(t)}</td><td>{chip(st)}</td><td>{e(c)}</td><td>{e(eg)}</td></tr>'
                   for i, (t, st, c, eg) in enumerate(entries))
    return (f'<div class="section"><h3>History · {e(site)}</h3></div><div class="tablewrap" style="flex:2"><table>'
            f'<tr><th style="width:18%">Time</th><th style="width:22%">Verdict</th><th>Change</th><th style="width:15%">Egress</th></tr>{rows}</table></div>')


def main_window(s: dict, extra_tabs=(), overlay: str = "") -> str:
    return (f'<div class="win">{titlebar("CertWatch")}{header(s["dark"])}{tabs(s["tab"], extra_tabs)}'
            f'<div class="content">{card(s)}{sites_table(s["sites"], s["sel_site"], s["tab"])}{history_table(s["history"], s["sel_hist"])}</div></div>{overlay}')


PICKER_CSS = """
.dialog{position:absolute;left:371px;top:150px;width:440px;background:var(--surface);border:1px solid var(--line-strong);
  box-shadow:0 8px 28px rgba(0,0,0,.45)}
.dialog .body{padding:16px}
.dialog h2{font-size:14px;font-weight:600;color:var(--heading)}
.dialog p{color:var(--muted);margin:4px 0 12px}
.list{border:1px solid var(--line-strong);border-radius:4px;overflow:hidden}
.row{height:40px;display:flex;align-items:center;padding:0 12px;border-top:1px solid var(--line)}
.row:first-child{border-top:none}
.row.hover{background:var(--surface-alt)}
.box{width:16px;height:16px;border:1px solid var(--line-strong);border-radius:2px;display:flex;align-items:center;justify-content:center;flex:none}
.box.on{background:var(--primary);border-color:var(--primary)}
.row .nm{margin-left:12px}
.row .st{margin-left:auto;font-size:11px}
.row .st.run{background:var(--neutral-tint);color:var(--on-neutral-tint);border-radius:2px;padding:1px 6px}
.row .st.off{color:var(--muted)}
.foot{display:flex;gap:6px;margin-top:12px}
.foot .r{margin-left:auto;display:flex;gap:8px}
"""


def picker() -> str:
    # Matches the tabs behind it: Edge, Brave and Chrome live, Firefox chosen but not running.
    browsers = [("Edge", True, True), ("Chrome", True, True), ("Brave", True, True), ("Island", False, False),
                ("Vivaldi", False, False), ("Opera", False, False), ("Firefox", True, False)]
    rows = []
    for i, (name, on, running) in enumerate(browsers):
        mark = '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="var(--on-primary)" stroke-width="2"><path d="M3,8 L6.5,11.5 L13,4.5"/></svg>' if on else ""
        st = '<span class="st run">Running</span>' if running else '<span class="st off">Not running</span>'
        rows.append(f'<div class="row{" hover" if i == 6 else ""}"><span class="box{" on" if on else ""}">{mark}</span><span class="nm">{e(name)}</span>{st}</div>')
    n = sum(1 for b in browsers if b[1])
    return (f'<div class="dialog">{titlebar("Choose browsers", close_only=True)}<div class="body"><h2>Choose browsers to monitor</h2>'
            '<p>Tick as many as you like. CertWatch reads the address bar of these browsers only, and never sits in their traffic.</p>'
            f'<div class="list">{"".join(rows)}</div><div class="foot"><span class="btn small">Select all</span><span class="btn small">Clear</span>'
            f'<span class="r"><span class="btn" style="min-width:80px;justify-content:center">Cancel</span>'
            f'<span class="btn primary">Monitor {n} browsers</span></span></div></div></div>')


LOG_ROWS = [
    ("20:00:12.402", "Info", "edge", "crm.fabrikam.com: NotInspected, egress Wi-Fi Direct, change FirstSeen"),
    ("20:00:12.402", "Debug", "edge", "crm.fabrikam.com: issuer R11, root ISRG Root X1, ip 203.0.113.24, route 0.0.0.0/0 → via 10.0.0.1, reason: Default route via 10.0.0.1"),
    ("20:00:44.118", "Info", "edge", "crm.fabrikam.com: NotInspected, egress ZTNA Adapter Tunnel, change EgressChanged"),
    ("20:00:44.121", "Info", "notify", "Edge · traffic moved: crm.fabrikam.com now leaves via ZTNA Adapter: ZTNA · steered."),
    ("20:02:09.877", "Debug", "brave", "Address bar: news.adatum.com"),
    ("20:02:09.878", "Info", "brave", "Now on news.adatum.com"),
    ("20:02:10.031", "Info", "brave", "news.adatum.com: Inspected, egress ZTNA Adapter Tunnel, change InspectionStarted"),
    ("20:02:10.031", "Debug", "brave", "news.adatum.com: issuer Contoso Inspection CA, root Contoso Root CA, ip 100.64.0.51, route 100.64.0.0/10 → via 172.16.50.2"),
    ("20:02:10.036", "Info", "notify", "Brave · inspection found: news.adatum.com: ZTNA · inspected. Issuer: Contoso Inspection CA"),
    ("20:02:25.240", "Info", "chrome", "www.wikipedia.org: NotInspected, egress Wi-Fi Direct, change None"),
    ("20:03:02.514", "Debug", "firefox", "Address bar: (no window)"),
]


def log_window() -> str:
    rows = "".join(f'<tr><td>{t}</td><td>{l}</td><td>{s}</td><td style="white-space:normal">{e(m)}</td></tr>' for t, l, s, m in LOG_ROWS)
    box_on = ('<span class="box on" style="width:16px;height:16px;border-radius:2px;display:inline-flex;align-items:center;justify-content:center;'
              'background:var(--primary)"><svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="var(--on-primary)" stroke-width="2">'
              '<path d="M3,8 L6.5,11.5 L13,4.5"/></svg></span>')
    box_off = '<span style="width:16px;height:16px;border:1px solid var(--line-strong);border-radius:2px;display:inline-block"></span>'
    return (f'<div class="win" style="background:var(--surface)">{titlebar("CertWatch log")}'
            '<div style="display:flex;align-items:center;gap:8px;padding:8px 12px;background:var(--surface-alt);border-bottom:1px solid var(--line)">'
            f'{box_on}<span style="margin-right:12px">Debug detail</span>{box_off}<span style="margin-right:12px">Write log to file</span>'
            '<span style="color:var(--muted)">Filter</span><span class="input" style="width:200px"></span>'
            '<span style="margin-left:auto;display:flex;gap:6px"><span class="btn small">Open log folder</span><span class="btn small">Copy all</span>'
            '<span class="btn small">Clear log</span></span></div>'
            '<table class="mono" style="border:none;font-size:12px"><tr><th style="width:110px">Time</th><th style="width:56px">Level</th>'
            f'<th style="width:64px">Source</th><th>Message</th></tr>{rows}</table></div>')


def page(body: str, dark: bool, extra_css: str = "") -> str:
    return (f'<!doctype html><html><head><meta charset="utf-8"><style>:root{{{css_vars(dark)}}}{BASE_CSS}{extra_css}'
            f'.log td{{height:auto;padding:4px 10px}}</style></head><body class="log">{body}</body></html>'
            if "CertWatch log" in body else
            f'<!doctype html><html><head><meta charset="utf-8"><style>:root{{{css_vars(dark)}}}{BASE_CSS}{extra_css}</style></head>'
            f'<body>{body}</body></html>')


def build(name: str) -> tuple[str, int, int]:
    if name in SCENES:
        s = SCENES[name]
        return page(main_window(s), s["dark"]), 1182, 738
    if name == "picker":
        s = dict(SCENES["dark-steered"])
        firefox = (("Firefox", "Not running", "neutral"),)
        return page(main_window(s, firefox, picker()), True, PICKER_CSS), 1182, 738
    if name == "log":
        return page(log_window(), True), 1102, 488
    raise SystemExit(f"unknown mock-up: {name}")


def chrome_binary() -> str:
    if os.environ.get("CHROME"):
        return os.environ["CHROME"]
    shells = sorted(Path.home().glob(".cache/ms-playwright/chromium_headless_shell-*/*/chrome-headless-shell"))
    if not shells:
        raise SystemExit("No headless Chromium found; set CHROME=/path/to/chrome-headless-shell")
    return str(shells[-1])


def render(name: str, chrome: str, tmp: Path) -> None:
    doc, w, h = build(name)
    src = tmp / f"{name}.html"
    src.write_text(doc, encoding="utf-8")
    out = OUT / f"{name}.png"
    subprocess.run([chrome, "--headless", "--disable-gpu", "--hide-scrollbars", "--no-sandbox",
                    f"--force-device-scale-factor={SCALE}", f"--window-size={w},{h}",
                    f"--screenshot={out}", src.as_uri()], check=True, capture_output=True, timeout=60)
    print(f"{out.relative_to(ROOT)}  ({w * SCALE}x{h * SCALE})")


if __name__ == "__main__":
    names = sys.argv[1:] or [*SCENES, "picker", "log"]
    chrome = chrome_binary()
    with tempfile.TemporaryDirectory() as d:
        for n in names:
            render(n, chrome, Path(d))
