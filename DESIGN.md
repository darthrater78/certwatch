---
version: alpha
name: CertWatch
description: Compact Windows desktop tool that shows, live, whether a browser's traffic is steered to ZTNA and whether its TLS certificate was replaced for inspection.
colors:
  brand: "#1F2A44"
  on-brand: "#FFFFFF"
  on-brand-muted: "#C5CCDB"
  ground: "#EEF0F4"
  surface: "#FFFFFF"
  surface-alt: "#F7F8FA"
  line: "#DDE1E7"
  line-strong: "#C9CDD3"
  on-surface: "#1B1B1B"
  on-surface-muted: "#5C5C5C"
  heading: "#1F2A44"
  inspected: "#5B3FC4"
  inspected-tint: "#EEEAFB"
  on-inspected-tint: "#3F2A99"
  steered: "#0A5FB4"
  steered-tint: "#E6F0FA"
  on-steered-tint: "#084A8C"
  direct: "#0E6B5C"
  direct-tint: "#E5F3EF"
  on-direct-tint: "#0B5546"
  untrusted: "#B3261E"
  untrusted-tint: "#FBE9E7"
  on-untrusted-tint: "#8C1D17"
  neutral-tint: "#EEEFF1"
  on-neutral-tint: "#454545"
  on-status: "#FFFFFF"
  ground-dark: "#0A0A0B"
  surface-dark: "#111113"
  surface-alt-dark: "#18181B"
  line-dark: "#26262B"
  line-strong-dark: "#3A3A41"
  on-surface-dark: "#F2F2F4"
  on-surface-muted-dark: "#9E9EA8"
  heading-dark: "#F2F2F4"
  brand-dark: "#000000"
  inspected-tint-dark: "#221C3A"
  on-inspected-tint-dark: "#C9BDFF"
  steered-tint-dark: "#10223A"
  on-steered-tint-dark: "#9EC7F7"
  direct-tint-dark: "#0F2621"
  on-direct-tint-dark: "#8FD6C2"
  untrusted-tint-dark: "#2E1414"
  on-untrusted-tint-dark: "#F2AAA3"
  neutral-tint-dark: "#1E1E22"
  on-neutral-tint-dark: "#C6C8CC"
  on-brand-muted-dark: "#B3B3BB"
  inspected-dark: "#7B61F0"
  steered-dark: "#2F86E8"
  direct-dark: "#20A088"
  untrusted-dark: "#E0483F"
  selected-row-dark: "#23232A"
typography:
  body-md:
    fontFamily: Segoe UI Variable Text
    fontSize: 13px
    fontWeight: 400
    lineHeight: 1.35
  label-sm:
    fontFamily: Segoe UI Variable Text
    fontSize: 11px
    fontWeight: 400
    lineHeight: 1.3
  title-md:
    fontFamily: Segoe UI Variable Text
    fontSize: 14px
    fontWeight: 600
    lineHeight: 1.3
  verdict-lg:
    fontFamily: Segoe UI Variable Display
    fontSize: 20px
    fontWeight: 600
    lineHeight: 1.25
  mono-md:
    fontFamily: Cascadia Mono
    fontSize: 12px
    fontWeight: 400
    lineHeight: 1.35
rounded:
  none: 0px
  sm: 2px
  md: 4px
  icon: 14px
spacing:
  xs: 4px
  sm: 8px
  md: 12px
  lg: 16px
  row: 30px
  control: 32px
components:
  app-header:
    backgroundColor: "{colors.brand}"
    textColor: "{colors.on-brand}"
    height: 36px
  app-header-dark:
    backgroundColor: "{colors.brand-dark}"
    textColor: "{colors.on-brand}"
  card:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.md}"
  card-dark:
    backgroundColor: "{colors.surface-dark}"
    textColor: "{colors.on-surface-dark}"
  verdict-header-inspected:
    backgroundColor: "{colors.inspected-tint}"
    textColor: "{colors.on-inspected-tint}"
    typography: "{typography.verdict-lg}"
  verdict-header-inspected-dark:
    backgroundColor: "{colors.inspected-tint-dark}"
    textColor: "{colors.on-inspected-tint-dark}"
  verdict-tile-inspected:
    backgroundColor: "{colors.inspected}"
    textColor: "{colors.on-status}"
    size: 40px
    rounded: "{rounded.md}"
  verdict-tile-steered:
    backgroundColor: "{colors.steered}"
    textColor: "{colors.on-status}"
  verdict-tile-direct:
    backgroundColor: "{colors.direct}"
    textColor: "{colors.on-status}"
  verdict-tile-untrusted:
    backgroundColor: "{colors.untrusted}"
    textColor: "{colors.on-status}"
  chip-inspected:
    backgroundColor: "{colors.inspected-tint}"
    textColor: "{colors.on-inspected-tint}"
    rounded: "{rounded.sm}"
    typography: "{typography.label-sm}"
  chip-inspected-dark:
    backgroundColor: "{colors.inspected-tint-dark}"
    textColor: "{colors.on-inspected-tint-dark}"
  chip-steered:
    backgroundColor: "{colors.steered-tint}"
    textColor: "{colors.on-steered-tint}"
  chip-steered-dark:
    backgroundColor: "{colors.steered-tint-dark}"
    textColor: "{colors.on-steered-tint-dark}"
  chip-direct:
    backgroundColor: "{colors.direct-tint}"
    textColor: "{colors.on-direct-tint}"
  chip-direct-dark:
    backgroundColor: "{colors.direct-tint-dark}"
    textColor: "{colors.on-direct-tint-dark}"
  chip-untrusted:
    backgroundColor: "{colors.untrusted-tint}"
    textColor: "{colors.on-untrusted-tint}"
  chip-untrusted-dark:
    backgroundColor: "{colors.untrusted-tint-dark}"
    textColor: "{colors.on-untrusted-tint-dark}"
  chip-neutral:
    backgroundColor: "{colors.neutral-tint}"
    textColor: "{colors.on-neutral-tint}"
  chip-neutral-dark:
    backgroundColor: "{colors.neutral-tint-dark}"
    textColor: "{colors.on-neutral-tint-dark}"
  button:
    backgroundColor: "{colors.surface-alt}"
    textColor: "{colors.on-surface}"
    rounded: "{rounded.md}"
    height: 32px
  button-dark:
    backgroundColor: "{colors.surface-alt-dark}"
    textColor: "{colors.on-surface-dark}"
  button-primary:
    backgroundColor: "{colors.brand}"
    textColor: "{colors.on-brand}"
  button-primary-dark:
    backgroundColor: "{colors.heading-dark}"
    textColor: "{colors.brand-dark}"
  table-row:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface}"
    height: 30px
  table-row-selected:
    backgroundColor: "{colors.inspected-tint}"
    textColor: "{colors.on-surface}"
  table-row-selected-dark:
    backgroundColor: "{colors.selected-row-dark}"
    textColor: "{colors.on-surface-dark}"
  muted-text:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.on-surface-muted}"
  muted-text-dark:
    backgroundColor: "{colors.surface-dark}"
    textColor: "{colors.on-surface-muted-dark}"
---

## Overview

A tool a sales engineer keeps open beside the browser during live ZTNA demos, read at a glance and on a shared screen. It shows one thing per browser: whether traffic is steered into the ZTNA tunnel, and whether the certificate was replaced for inspection. Dense, factual, Windows-native.

## Colors

Status colours carry meaning and nothing else: violet = inspected, blue = steered to ZTNA but not inspected, teal = direct, red = untrusted. Inspection is not red, because in a ZTNA demo it means the product is working. Status colours appear as a solid 40px tile (white symbol), as tinted chips (tint background, dark ink), and as path-diagram connectors. Slate (`brand`) is the identity colour: app header, headings, primary button. Two themes: Flashbang (bright, the base tokens) and Dark (`*-dark` tokens, falling back to the base token where none is given). Dark is "Ink": near-black neutrals with no blue cast, colour only where it carries meaning, and brighter status colours so they hold up on black; the selected table row is a neutral lift rather than a tint. Every control, including scrollbars, tooltips, menus, checkboxes and table headers, takes its colours from the active theme; none keeps Windows' light defaults.

## Typography

Segoe UI Variable throughout, chosen to match Windows 11, at 13px body with 11px labels and 14px section titles. The verdict headline is the only large text (20px, Display optical size). Cascadia Mono for routes, IPs and fingerprints so they align and read unambiguously.

## Layout

8px grid. Window padding 12px. Table rows 30px, controls 32px tall, chips 18px. No empty bands: the card is header (verdict) → path diagram → 4-column fact grid; then sites, then history, each directly under its title. Sites and history share the height left under the card (3:2), so the page always fills the window; below 600px the page scrolls. Tabs carry the browser name plus a one-line live status so every browser's state is visible without switching.

## Elevation & Depth

Flat. Depth comes from tone and 1px borders (`line`), never shadows. The only layered elements are the selected tab (surface colour, merges into the content) and the selected table row (tint plus a 2px inset bar in the status colour).

## Shapes

4px corners on cards, buttons, inputs, tiles and path nodes; 2px on chips; square table rows. The app icon alone uses a 14/64 rounded tile.

## Components

- App header: slate bar, 36px, icon + name + version + GitHub and release-notes links, then Browsers…, Clear all…, Log, Pop-ups, Theme.
- Browser picker: a 440px dialog (Surface, 16px padding) opened by the header's Browsers… and on first run. Title 14px semibold in Heading, one muted line saying only these browsers' address bars are read. Then a list in a 1px LineStrong frame with 4px corners: one 40px row per browser, the whole row clickable, with a 16px checkbox, the name, and on the right a neutral "Running" chip or muted "Not running" text; rows divided by 1px Line, hover lifts to SurfaceAlt. Footer: Select all and Clear (small buttons) on the left; Cancel and a primary button that counts the choice ("Monitor 3 browsers") on the right. Several browsers are ticked before anything happens: tabs change only on Apply, and Cancel changes nothing.
- Empty page (no tabs): centred muted 15px line. With nothing ticked it says no browsers are monitored and shows a primary "Choose browsers…" button; once something is ticked it names the browsers being waited for.
- Verdict card: tinted header with the 40px status tile, site and context line (11px), verdict headline (20px), actions on the right; path diagram of 24px-icon nodes joined by 2px status-coloured arrows; 4-column fact grid (label 11px muted, value 13px, mono for technical values).
- Pop-up: 380px card in the bottom-right corner of the work area, Surface with a 1px LineStrong border and rounded corners; 40px status tile, title 14px semibold, text 13px, "CertWatch · click to show <site>" label (a click opens that browser's tab with the site selected); dismiss ✕. Up to 4 stack upward, newest at the bottom. They stay until dismissed by default; the header's "Pop-ups" button cycles until dismissed / 10 s / 30 s / 1 min.
- Chips: icon + short text ("ZTNA · inspected", "ZTNA · steered", "Direct", "Untrusted").
- Tables: header row on `surface-alt`, 12px muted labels; rows 30px with 1px dividers; selected row tinted with an inset bar. Clicking a row (site or history) shows it on the card; "Back to live" returns.

## Do's and Don'ts

- Do keep every colour in the theme dictionaries; no hex in views or code-behind.
- Do keep text at 4.5:1 or better in both themes, including chips and muted labels.
- Don't use pill shapes, gradients, glass, shadows, coloured left borders on cards, emoji, or all-caps labels.
- Don't use red for inspection.
- Don't add empty spacer bands; if a section has nothing to show, collapse it.
