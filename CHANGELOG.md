# Changelog

All notable changes to CertWatch. Versions are the UTC date of the release.

## [Unreleased]

### Added

- Browser picker (first run, and **Browsers…** in the header): tick any number of browsers, then
  apply. Every chosen browser gets a tab right away, marked **Not running** until it starts. The
  choice is saved; dropping one stops reading its address bar and removes its tab, keeping its
  history.

### Changed

- First run monitors nothing until you pick browsers in the picker (earlier builds followed every
  running browser automatically). `--browser` still adds a browser for that run.

### Security

- The debug log records only the host checked, never the full address-bar text, which could hold
  paths and query strings.
- The README now opens with **How it works**: CertWatch reads the address bar and makes its own
  separate TLS handshake. It never sits in the browser's path, and it doesn't see other tabs or the
  other hosts a page loads from.

## [2026.11.17] - 2026-11-17

First release.

### Added

- One window with a tab per running browser (Edge, Chrome, Brave, Island, Vivaldi, Opera, Firefox),
  each following that browser's active tab through Windows UI Automation, without an extension.
- Inspection verdict from the certificate chain: public root (Mozilla bundle, matched by key) or
  Certificate Transparency proof means the site's own certificate; a private root this machine
  trusts means a proxy replaced it, with the vendor named when recognised (Island, Zscaler,
  Netskope, Palo Alto and others).
- Steering verdict from the Windows routing table: ZTNA tunnel or direct, by the egress adapter's
  friendly name, with the route shown. Combined labels: steered and inspected, steered but not
  inspected, inspected on this device, direct.
- Path diagram on the card (browser → tunnel or direct → inspection → site), fact grid, and notes
  for mTLS sites and earlier inspection. A steered mTLS site's headline says it needs a bypass.
- Per-site history (each site listed once); click a site or a history entry to show it on the card.
- Pop-ups (CertWatch's own, so Do not disturb can't hide them) when a site is steered to ZTNA, when
  inspection is found or stops, the inspecting CA changes, traffic moves to another adapter, or a
  site uses mTLS; clicking one opens that site in its browser's tab. They stay until dismissed by
  default; "Pop-ups" in the header switches to 10 s, 30 s or 1 min. "Show test pop-up" is in the
  tray menu.
- Log window with debug detail and optional file logging. "Clear browser" in each tab clears that
  browser's history; "Clear all" in the header clears every browser's history and the log.
- Two themes, Flashbang (bright) and Dark, switched from the header (the first run matches
  Windows); app icon; root export (.cer, .pem or full chain).
- One self-contained, compressed exe (about 63 MB) that needs no installer or .NET install.

### Security

- History encrypted at rest (AES-256-GCM, master key protected by Windows DPAPI).
- Self-contained build bundles the .NET 10.0.15 security release.
- Each release exe has a signed build provenance attestation (`gh attestation verify`); pull
  requests that add a vulnerable package fail dependency review.
