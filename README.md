# CertWatch

<img src="src/CertWatch.App/Assets/CertWatch-256.png" width="64" align="right" alt="CertWatch icon">

**[GitHub](https://github.com/darthrater78/certwatch)** · **[Release notes for v2026.11.16](https://github.com/darthrater78/certwatch/releases/tag/v2026.11.16)** · [Changelog](CHANGELOG.md)

A Windows app for ZTNA demos. For every browser that's running, it shows, live, whether the site in
the active tab is **steered into the ZTNA tunnel** and whether its certificate was **replaced for
inspection**, with the path drawn out (browser → tunnel or direct → inspection → site). It calls
out changes as Windows notifications, flags mTLS sites, and keeps an encrypted per-site history.

## Run (on Windows)

Run `CertWatch.exe`. Only one copy runs; starting it again brings the window forward. There's a tab
for every supported browser that's running (Edge, Chrome, Brave, Island, Vivaldi, Opera, Firefox).
Tabs appear as browsers start and stay after they close, and each follows its own browser, even in
the background. Each tab header shows that browser's current site and verdict.

In a tab:

- **The card** shows the live verdict for the active site: the path diagram, issuer, root, validity,
  protocol, egress adapter, route, the IP CertWatch connected to, and the SHA-256 fingerprint.
  **Re-check**, **Copy SHA-256** and **Export root…** (the root as `.cer`/`.pem`, or the full chain)
  are on it.
- **Sites** lists every site seen in that browser once, with its current label, issuer, egress, and
  whether it was **inspected earlier** even if it isn't now. Filter it by name.
- **History** shows the selected site's first visit and every change. **Click a site or a history
  entry** to show that saved result on the card; **Back to live** returns.
- **Clear site** clears one site for that browser. **Clear everything…** clears all history in
  every browser and the log.

In the header: **GitHub** and **Release notes** links, **Log**, and **Theme** (System, Light, Dark;
System follows Windows). The **Log** window has **Debug detail** (address-bar reads, routes, issuers,
DevTools traffic) and **Write log to file** (off by default), plus Open log folder, Copy all and
Clear log.

Closing the window keeps CertWatch in the tray (the icon takes the latest verdict's colour); exit
from the tray menu, which also has **Send test notification**.

Optional command-line switches:

```
CertWatch.exe --browser island                  # always show this tab, even before Island starts
CertWatch.exe --process SomeBrowser.exe         # add a tab for a browser not in the built-in list
CertWatch.exe --interval 10                     # re-check each current site every 10 s (default 15)
```

## Launch for demo (optional exact mode)

On a Chrome, Edge or Brave tab, **Launch for demo** opens a separate window of that browser with
its own CertWatch profile (`%LOCALAPPDATA%\CertWatch\profiles\<browser>`), connected to CertWatch
over a private DevTools pipe, and adds a **"<browser> · demo"** tab for it. That tab reads the exact
certificate the browser received and checks **every open tab** in the demo window. A separate profile
is required because Chrome 136+ refuses remote debugging on the normal profile. Your normal windows
keep being tracked on the normal tab. Closing the demo window ends it; the demo tab stays with its
history.

## How it decides

Two independent measurements, combined into one label per site:

- **Inspection:** if the certificate chains to a public root (Mozilla bundle, matched by key so
  cross-signed roots count) or is publicly logged (Certificate Transparency), it's the site's own:
  **not inspected**. If it chains to a private root this machine trusts, a proxy replaced it:
  **inspected** (vendor named when recognised).
- **Steering (egress):** Windows is asked which route and interface reach the site's IP. A /32 host
  route with no gateway, a specific route through a gateway on an adapter that has no default
  gateway (how Island Private Access does it), a tunnel interface, or a known ZTNA/VPN adapter means
  **tunnelled to ZTNA**; otherwise **direct**. The adapter's friendly name is shown.

Labels: **Steered to ZTNA · inspected**, **Steered to ZTNA · not inspected**, **Inspected on this
device** (certificate replaced, traffic not tunnelled), **Direct · not inspected**. Sites inspected
earlier stay marked "Was inspected". Servers that ask for a client certificate are flagged **mTLS**.

Without Launch for demo, the check is CertWatch's own connection from this machine. A browser that
routes or inspects its own traffic differently may see something else; in exact mode CertWatch
shows when the browser's certificate differs from its own connection's.

## Security architecture

How CertWatch is built with regard to security, in statements you can check against the code.

### Design

- **What it reads.** The active tab's address comes from the browser's address bar through Windows
  UI Automation (read-only). Site certificates come from CertWatch's own TLS connection to that
  site; in **Launch for demo** mode, also from the demo browser over DevTools. Routes come from
  Windows' routing table (`GetBestRoute2`). All of it is treated as untrusted data: addresses are
  parsed and only `https` hosts are checked, and notification text is passed to Windows as plain
  strings, never as markup.
- **What it trusts.** Nothing it observes. CertWatch's own TLS connection accepts any certificate
  on purpose, because its job is to record what was presented. It sends no application data over
  that connection, and it requires TLS 1.2 or 1.3. Verdicts compare the chain against the bundled
  Mozilla root list (`src/CertWatch.Core/Resources/public-roots.pem`, from curl.se), matched by
  public key, and against Certificate Transparency proofs.
- **Privileges.** Runs as the signed-in user (`asInvoker`), never elevated. It writes only under
  `%LOCALAPPDATA%\CertWatch`, and nothing to the registry. P/Invoke calls load system DLLs from
  System32 only.
- **What it stores, and how it's protected (encryption at rest).**
  - `history.db`: sites visited per browser, with IPs, certificate details and egress. Everything
    that identifies a site or connection is encrypted with **AES-256-GCM**. Rows are found by an
    HMAC-SHA256 of the site, so site names aren't stored in the clear. Only timestamps, the browser
    name, the verdict, the change type, the mTLS flag and the egress kind are plain. SQLite
    `secure_delete` is on, and clearing history also truncates the write-ahead log.
  - `history.key`: the random 32-byte master key for the above, protected with **Windows DPAPI**
    under the current user's account, so only that account on that PC can unwrap it. History that
    can't be decrypted (for example, copied from another account) is set aside and a fresh database
    started.
  - `window.json`, `settings.json`: window position, theme and log options, plain (not sensitive).
  - `logs\certwatch-<date>.log`: written **only** when you turn on **Write log to file**. Plain text,
    and it names the sites you visit, so it's off by default; **Clear log** and **Clear everything**
    delete these files. Without it, the log lives only in memory (last 5,000 entries).
  - `profiles\<browser>`: the demo browser's own profile. It holds whatever you sign into in the
    demo window, protected by that browser's own storage protections.
- **Network exposure.** CertWatch listens on nothing. Outbound, it makes one TLS handshake per site
  you visit, through the system proxy when one is set. In demo mode it talks to the browser it
  started over `--remote-debugging-pipe`: two anonymous pipes that only CertWatch and that browser
  hold, with the browser limited to inheriting exactly those two handles. There is no debugging
  port, so no other program on the PC can control the demo browser.
- **Dependencies and releases.** NuGet packages are pinned and audited at every security gate
  (`dotnet list package --vulnerable --include-transitive`), and Dependabot watches them. The
  project stays on the .NET 10 LTS line, and the self-contained build pins the newest .NET security
  patch (`RuntimePatch` in `CertWatch.App.csproj`). The Mozilla root bundle is refreshed from
  curl.se by hand; check its date before each release. `CertWatch.exe` is not code-signed yet
  (accepted for an internal demo tool, 2026-11-16).

### Changes

- 2026-11-16: Added the log. It's in memory by default; writing it to disk is opt-in, and "Clear
  everything" deletes it along with all history.
- 2026-11-16: History is encrypted at rest (AES-256-GCM, key protected by DPAPI). Plaintext history
  from earlier builds is migrated and the old pages are wiped.
- 2026-11-16: Demo mode uses a private DevTools pipe instead of a local debugging port, which any
  program on the PC could have reached.
- 2026-11-16: Notifications use Windows' built-in API instead of an unmaintained package
  (Microsoft.Toolkit.Uwp.Notifications, last released 2022).
- 2026-11-16: The tray icon and notifications call Windows directly (`Shell_NotifyIcon`) instead of
  WinForms and the WinRT toast API. That drops ~50 MB of bundled code and the per-user
  `AppUserModelId` registry key earlier builds wrote.
- 2026-11-16: The self-contained build bundles .NET 10.0.15 (a security release) instead of
  10.0.14. Microsoft.Data.Sqlite moved from the .NET 11 (STS) line to 10.0.15 (LTS).
- 2026-11-16: CertWatch's own TLS connection now requires TLS 1.2+. P/Invoke is limited to System32.
  A browser path found in the registry is only used if it's that browser's own program file.

## Build (Linux or Windows)

```
scripts/build.sh test       # build the solution and run the tests
scripts/build.sh publish    # self-contained single-file win-x64 build → artifacts/CertWatch-v<version>-win-x64.exe
scripts/build.sh            # both
```

The WPF app compiles on Linux (via `EnableWindowsTargeting`) and must run on Windows. Core tests run
anywhere.

- `src/CertWatch.Core`: cross-platform. TLS check, verdict, egress, encrypted history, DevTools pipe client.
- `src/CertWatch.App`: Windows-only WPF app. UI Automation tracking, window, themes, tray, notifications, log.
- `tests/CertWatch.Core.Tests`: xUnit, including a local TLS server (chain and mTLS) and a fake browser over pipes.
- `tools/make_icon.py`, `tools/gen_theme.py`: regenerate the icon and the theme files from `DESIGN.md`.

### CI and releases

- **CI** (`.github/workflows/ci.yml`) runs `scripts/build.sh` on every push and pull request, checks
  for vulnerable packages, and uploads the Windows exe itself, not zipped, as an artifact (kept 14 days). Docs-only
  changes skip the build.
- **Release** (`.github/workflows/release.yml`) runs when a `v*` tag is pushed. It checks that the tag
  is on `main` (pre-release tags such as `-dev.1` excepted), that CI passed for that commit, and that
  the tag matches `<Version>` in `CertWatch.App.csproj`. It then builds the exe and publishes the
  GitHub release with the matching `CHANGELOG.md` section as notes. Pre-release tags publish as
  pre-releases.
- **Lint workflows** (`.github/workflows/lint-workflows.yml`) runs actionlint on workflow changes.
- Dependabot keeps the NuGet packages and the action pins current.
