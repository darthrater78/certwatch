# Changelog

All notable changes to CertWatch. Versions are the UTC date of the release.

## [2026.11.16] - 2026-11-16

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
  for mTLS sites, earlier inspection, and browser/own-connection mismatches.
- Per-site history (each site listed once); click a site or a history entry to show it on the card.
- Windows notifications when inspection is found or stops, the inspecting CA changes, traffic moves
  to another adapter, or a site uses mTLS; "Send test notification" in the tray menu.
- Launch for demo: a separate browser window over a private DevTools pipe, in its own tab, with the
  exact certificate the browser received and every open tab checked.
- Log window with debug detail and optional file logging; "Clear everything" for all history and the log.
- Light and dark themes (or follow Windows), app icon, root export (.cer, .pem or full chain).

### Security

- History encrypted at rest (AES-256-GCM, master key protected by Windows DPAPI).
- No debugging port: the demo browser is reached over pipes only CertWatch holds.
- Self-contained build bundles the .NET 10.0.15 security release.
