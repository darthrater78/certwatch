# CertWatch

Windows app for ZTNA demos. It follows one browser's active tab and shows whether the site's
certificate is the site's own (**not inspected**) or was replaced by an inspection proxy
(**inspected**). It calls out changes, flags mTLS sites, and keeps a per-site history.

## Run (on Windows)

Just run `CertWatch.exe` (only one copy runs; starting it again brings the window forward). One window, with a tab for every supported browser that's running
(Edge, Chrome, Brave, Island, Vivaldi, Opera, Firefox). Tabs appear as browsers start and stay after
they close. Each tab follows its own browser, even in the background.

Optional:

```
CertWatch.exe --browser island                  # always show this tab, even before Island starts
CertWatch.exe --process SomeBrowser.exe         # add a tab for a browser not in the built-in list
CertWatch.exe --interval 10                     # re-check each current site every 10 s (default 15)
```

Closing the window keeps CertWatch in the tray; exit from the tray menu. Pop-ups name the browser
they're about. History is kept, encrypted, in `%LOCALAPPDATA%\CertWatch\history.db`.

## Launch for demo (optional exact mode)

On a Chrome, Edge or Brave tab, **Launch for demo** opens that browser with a separate CertWatch
profile (`%LOCALAPPDATA%\CertWatch\profiles\<browser>`) and a private DevTools pipe. While it runs,
CertWatch reads the exact certificate the browser received and checks **every open tab** in that
window. A separate profile is required because Chrome 136+ refuses remote debugging on the normal
profile. Your normal browser windows keep being tracked the original way. Closing the demo browser
returns the tab to normal mode.

## How it decides

Two independent measurements, combined into one label per site:

- **Inspection:** if the certificate chains to a public root (Mozilla bundle, matched by key so
  cross-signed roots count) or is publicly logged (Certificate Transparency), it's the site's own:
  **not inspected**. If it chains to a private root this machine trusts, a proxy replaced it:
  **inspected** (vendor named when recognised).
- **Steering (egress):** Windows is asked which route and interface reach the site's IP. A /32 host
  route with no gateway, a tunnel interface, or a known ZTNA/VPN adapter means **tunnelled to ZTNA**;
  otherwise **direct**. The adapter's friendly name is shown.

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
  parsed and only `https` hosts are checked, and every value shown in a notification is
  XML-escaped.
- **What it trusts.** Nothing it observes. CertWatch's own TLS connection accepts any certificate
  on purpose, because its job is to record what was presented. It sends no application data over
  that connection, and it requires TLS 1.2 or 1.3. Verdicts compare the chain against the bundled
  Mozilla root list (`src/CertWatch.Core/Resources/public-roots.pem`, from curl.se), matched by
  public key, and against Certificate Transparency proofs.
- **Privileges.** Runs as the signed-in user (`asInvoker`), never elevated. It writes only under
  `%LOCALAPPDATA%\CertWatch` and one per-user registry key
  (`HKCU\Software\Classes\AppUserModelId\CertWatch.App`, so Windows can show its notifications).
  P/Invoke calls load system DLLs from System32 only.
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
  - `window.json`: window size and position, plain (not sensitive).
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

- 2026-11-16: History is encrypted at rest (AES-256-GCM, key protected by DPAPI). Plaintext history
  from earlier builds is migrated and the old pages are wiped.
- 2026-11-16: Demo mode uses a private DevTools pipe instead of a local debugging port, which any
  program on the PC could have reached.
- 2026-11-16: Notifications use Windows' built-in API instead of an unmaintained package
  (Microsoft.Toolkit.Uwp.Notifications, last released 2022).
- 2026-11-16: The self-contained build bundles .NET 10.0.15 (a security release) instead of
  10.0.14. Microsoft.Data.Sqlite moved from the .NET 11 (STS) line to 10.0.15 (LTS).
- 2026-11-16: CertWatch's own TLS connection now requires TLS 1.2+. P/Invoke is limited to System32.
  A browser path found in the registry is only used if it's that browser's own program file.

## Build (Linux or Windows)

```
dotnet build                     # whole solution (the WPF app compiles on Linux via EnableWindowsTargeting)
dotnet test                      # core tests run anywhere
dotnet publish src/CertWatch.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

- `src/CertWatch.Core`: cross-platform. TLS check, verdict, history (SQLite), site monitor.
- `src/CertWatch.App`: Windows-only WPF app. UI Automation tracking, window, tray, notifications.
- `tests/CertWatch.Core.Tests`: xUnit, including a local TLS server for chain and mTLS detection.
