# Open Azure P2S VPN Client

**Connect to several Azure Point-to-Site VPNs at the same time on Windows, signing in with Microsoft Entra ID.**

The official Azure VPN Client only allows one connection at a time. If you work across several
Azure virtual networks (dev, test, production, customer environments...), you have to keep
disconnecting and reconnecting. **P2S VPN** connects to all of them at once, each on its own
network adapter, with the same Entra ID sign-in you already use.

- **Several VPNs at once.** Each profile gets its own adapter and routes.
- **Entra ID sign-in** in your normal browser (MFA and Conditional Access apply as usual). Your session is
  cached per Windows user and renewed silently.
- **Private DNS that works.** DNS servers pushed by the gateway are used for private Azure domains
  (App Service, PostgreSQL, Key Vault, Storage, ... and any domains you add), the way the official client does.
- **Simple app.** Drop your `azurevpnconfig.xml` (or the profile `.zip` from the Azure portal) onto
  the window, flip the switch. Status, address, traffic, routes, DNS and a live log are right there.
- **Starts with Windows** and can auto-connect chosen connections when you sign in.
- **No admin prompts to connect.** A small Windows service does the privileged work; you only approve
  the installer once.

> P2S VPN is an independent open-source project. It is not made, endorsed or supported by
> Microsoft or OpenVPN Inc. Check that using a third-party VPN client is allowed by your organisation.

## Install

1. Download `P2SVpn.exe` from the [latest release](../../releases/latest).
2. Run it and click **Install**. Windows asks for administrator approval once.
   Setup installs:
   - the app into `C:\Program Files\P2SVpn`, with a Start menu entry;
   - the *P2S VPN Service*;
   - the official, signed OpenVPN **TAP-Windows6** network driver (skipped if already present).
3. The app opens. Drag in your `azurevpnconfig.xml` and turn the connection on.

The exe is not code-signed yet, so the first time Windows SmartScreen may say *"Windows protected your PC"*.
Choose **More info → Run anyway**, or build it yourself (below).

To update, run a newer `P2SVpn.exe` and click **Update**. To remove it, use
*Settings → Apps → Installed apps → P2S VPN → Uninstall*.

### Requirements

- Windows 10 or 11, x64.
- An Azure **VPN Gateway Point-to-Site** profile with **Microsoft Entra ID** authentication
  (OpenVPN protocol). Certificate and RADIUS profiles are not supported.
- Your account must be allowed to use the VPN. If your tenant requires a *compliant device* for the
  VPN app, an unmanaged PC will be refused at sign-in, as with any client.

## Using it

| | |
|---|---|
| **Add a connection** | Drag `azurevpnconfig.xml` or the Azure profile `.zip` onto the window, or click **+ Add connection**. |
| **Connect / disconnect** | Flip the switch on the card, or use the button in the details panel or the tray menu. |
| **Rename** | **Rename** next to the status, or right-click a card. Profiles with the same VPN name are numbered and you are asked to rename them. |
| **Auto-connect** | "Connect automatically when I sign in to Windows" in the details panel. |
| **Sign in again** | When the cached session expires the app tells you; click **Sign in again** or reconnect. |
| **Logs** | The activity log under each connection. Files are in `C:\ProgramData\P2SVpn\logs`. |
| **Settings** | Start at sign-in (on by default), start minimized, notifications. |

Closing the window keeps P2S VPN running in the notification area, so connections stay up and
sign-ins can be renewed.

### Private DNS

When a gateway pushes a DNS server, P2S VPN adds Windows **NRPT** rules so lookups for these domains go to
that server only while the VPN is connected: `azurewebsites.net`, `azurestaticapps.net`,
`postgres.database.azure.com`, `mysql.database.azure.com`, `database.windows.net`, `documents.azure.com`,
`vault.azure.net`, `azurecr.io`, `eventgrid.azure.net`, `servicebus.windows.net`, `redis.cache.windows.net`,
and `blob` / `file` / `queue` / `table.core.windows.net`.

Add your organisation's own private domains (for example custom domains of internal apps) to
`C:\ProgramData\P2SVpn\dns-namespaces.txt` as an administrator. Changes apply on the next connect.

## How it works

```
 P2SVpn.exe (app, runs as you)                    P2S VPN Service (SYSTEM)
 ─────────────────────────────                    ────────────────────────
 • imports azurevpnconfig.xml                     • parses the profile itself, renders the
 • Entra sign-in (auth code + PKCE,     named        OpenVPN config (no free-form options)
   loopback redirect, browser)          pipe      • one TAP adapter per profile: P2S-<name>
 • caches refresh token (DPAPI)   ───────────▶    • one openvpn.exe per profile (--disable-dco)
 • sends: connect(profile, token)                 • gives the token to OpenVPN over its
 • shows status / log, renews tokens  ◀───────      management interface (username "AzureAD")
                                                  • NRPT rules for the pushed DNS server
```

Azure P2S with Entra ID is OpenVPN underneath: the Entra **access token is sent as the OpenVPN password**
(username `AzureAD`). A stock OpenVPN cannot carry it, because the token (~2–3 KB) is longer than two fixed
buffers. The release bundles OpenVPN 2.7.8 with a small patch raising those limits
(see [`openvpn/`](openvpn/)), cross-compiled from source by [`build/build.sh`](build/build.sh).

Some details that took a while to get right are documented in the code:

- **TAP, not DCO.** Azure gateways don't negotiate the `DATA_V2` packet format that OpenVPN's
  data-channel-offload driver needs, so each profile uses a TAP-Windows6 adapter.
- **One management command at a time.** OpenVPN 2.7's management interface can run a command twice and
  drop the next when several that print a reply arrive together, so the service waits for each
  `SUCCESS:`/`ERROR:` reply. The token goes in the multi-line base64 form (management version 5) in
  256-character lines, because longer lines can overflow OpenVPN's 1 KB input buffer.
- **Private DNS needs NRPT.** Windows asks every adapter's DNS server at once and takes the fastest
  answer, which is usually the home router's public one.

### Security model

- The service only accepts requests from local interactive users (named pipe ACL; network access denied).
- It never runs an OpenVPN config supplied by the user: it validates the Azure profile (gateway host
  names, tenant/app GUIDs, 512-hex server secret) and renders the config itself.
- The gateway certificate is checked against the DigiCert roots Azure uses (pinned by SHA-256) and
  against the gateway's expected certificate name.
- Access tokens are kept in memory only; the refresh token is encrypted with DPAPI for the Windows user.
- OpenVPN's management port listens on 127.0.0.1 with a random per-connection password.
- `C:\ProgramData\P2SVpn` is writable only by SYSTEM and administrators.

## Build from source

Builds run on Linux (or WSL) and produce one self-contained `dist/P2SVpn.exe`.

```bash
# Debian/Ubuntu
sudo apt-get install gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 make perl patch file \
                     autoconf automake libtool pkg-config curl
# plus the .NET 10 SDK: https://dotnet.microsoft.com/download

bash build/build.sh                 # → dist/P2SVpn.exe
VERSION=1.2.0 bash build/build.sh   # stamp a version
```

`build/build.sh` downloads OpenVPN, OpenSSL, the TAP-Windows6 header and the official OpenVPN installer,
**refuses any file whose SHA-256 differs** from [`build/versions.env`](build/versions.env), applies the
patch, cross-compiles OpenVPN (static OpenSSL) and publishes the .NET app.

GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) runs the same script on
every push and attaches `P2SVpn.exe` plus its SHA-256 to a GitHub Release when a `v*` tag is pushed:

```bash
git tag v1.1.0 && git push origin v1.1.0
```

### Repository layout

| Path | |
|---|---|
| `src/P2SVpn/` | The Windows app, service and installer (.NET 10, WinForms) |
| `openvpn/` | The OpenVPN patch and its Windows cross-build script |
| `build/` | `build.sh`, pinned versions/checksums, the icon generator |
| `scripts/windows/` | `p2s.ps1`: the original PowerShell client (same approach, no GUI) |
| `scripts/wsl/` | `p2s-ns`: run several [OpenP2S](https://github.com/wyruweso/openp2s) connections in WSL using network namespaces |

## Credits

- **[OpenP2S](https://github.com/wyruweso/openp2s)** by Mykhailo Tseluiko: the Linux Azure P2S Entra client
  whose research this builds on: token-as-password, the multi-line management password, and the
  OpenVPN patch used here.
- **[cveld/azure-vpn-client-headless-container](https://github.com/cveld/azure-vpn-client-headless-container)**:
  early reverse engineering of Azure's OpenVPN compatibility.
- **Luca Boccassi's** OpenVPN patches for long credentials.
- **[OpenVPN](https://openvpn.net/)** and **[OpenSSL](https://www.openssl.org/)**.

## License

The P2S VPN source code is licensed under the [MIT License](LICENSE).
Bundled third-party components keep their own licenses, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
In particular OpenVPN and the patch in `openvpn/` are GPL-2.0; their complete corresponding source is
the upstream tarball pinned in `build/versions.env` plus that patch.

*Azure, Microsoft Entra and Windows are trademarks of Microsoft. OpenVPN is a trademark of OpenVPN Inc.*
