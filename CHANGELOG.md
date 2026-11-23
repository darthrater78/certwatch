# Changelog

All notable changes to CertWatch. Versions are the UTC date of the release.

## [Unreleased]

### Added

- **Process flow**: choose **All processes**, a running process, or add one by name before it starts, and see where it
  connects, the path each connection takes, and the certificate chain, with each row labelled
  **Observed** or **Inferred**. **Clear list** resets the results for a clean retest. Started as
  administrator, the optional **Read this process's handshakes** reads real host names and TLS 1.2
  certificates passively and flags a process that received a different certificate. Off by default,
  scoped to the chosen process, and kept in memory only.
- **Pinned?** in Process flow: each destination reads **Pinning likely**, **Refused**, **No sign**,
  **Waiting** or **Can't tell**, with the reason and the next step beside it. With handshake reading
  on, a process that repeatedly refuses an inspected certificate this PC trusts, and never accepts
  one at that destination, reads **Pinning likely**. It's a behavioural signal, since an app with its
  own trust store looks the same. Results needing attention sort first, with filters and **Copy details**.
  The list names each app and its exe, and the certificate flow is coloured by what each step means.
- **Dock** in the header: keep CertWatch right or left of the browser (a 380px panel) or above it (a
  64px strip), docked to the browser window (moves with it) or to the screen edge (reserves space,
  so maximised windows fit around it). Docked, **Sites** lists only the page in focus; tick **Show
  all sites** for the rest. **Follow whichever browser is active** switches to the tab of
  the browser you click into, and **Keep on top** keeps the window above others. The choice is saved.
  Pop-ups are off while docked (still logged), since the verdict is already beside the browser.
  Screen-edge docking guards itself: if the reserved strip keeps re-placing itself, CertWatch gives
  the edge back and docks to the browser window instead for the rest of the session, and a run that
  didn't close cleanly while holding the edge starts docked to the browser next time.
- **Own-path detection**, for enterprise browsers with inspected results only: when CertWatch's check went through the ZTNA tunnel but the browser isn't
  connected to that address (an enterprise browser's own gateway),
  the card reads **Browser uses its own path** instead of presenting CertWatch's own result as the
  browser's, and the site's row shows **Own path**. It's read from Windows' TCP connection table;
  nothing is asked of the browser.
- **Enterprise browsers' own path**: when CertWatch's own check comes back inspected, it also checks the addresses the browser is connected to directly and shows the browser's
  own result (for example **Direct · not inspected**) with CertWatch's steered result as a note. Tray
  colour and pop-ups follow the browser's result.
- **Compare with the browser's certificate viewer**, offered on inspected enterprise-browser cards and when
  the browser takes its own path (off by default): open the browser's certificate viewer and
  CertWatch reads its issuer and fingerprint (read-only). A different certificate turns the headline
  into **Browser sees a different certificate**.

## [2026.11.17-2] - 2026-11-17

### Added

- Browser picker (first run, and **Browsers…** in the header): tick any number of browsers, then
  apply. Every chosen browser gets a tab right away, marked **Not running** until it starts. The
  choice is saved; dropping one stops reading its address bar and removes its tab, keeping its
  history.

### Changed

- First run monitors nothing until you pick browsers in the picker (earlier builds followed every
  running browser automatically). `--browser` still adds a browser for that run.

### Docs

- README screenshots redrawn for the new header, plus one of the browser picker. They're drawn by
  `tools/gen_mockups.py`, now kept in the repo.

### Security

- The debug log records only the host checked, never the full address-bar text, which could hold
  paths and query strings.
- The README now opens with **How it works**: CertWatch reads the address bar and makes its own
  separate TLS handshake. It never sits in the browser's path, and it doesn't see other tabs or the
  other hosts a page loads from.

## [2026.11.17] - 2026-11-17

First release.

### Added

- One window with a tab per running browser (Edge, Chrome, Brave, Vivaldi, Opera, Firefox and others),
  each following that browser's active tab through Windows UI Automation, without an extension.
- Inspection verdict from the certificate chain: public root (Mozilla bundle, matched by key) or
  Certificate Transparency proof means the site's own certificate; a private root this machine
  trusts means a proxy replaced it, with the vendor named when recognised (Zscaler,
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
