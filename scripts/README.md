# Extra scripts

These came before the app and are kept for people who prefer a command line. The app in `src/` is the
recommended way on Windows.

## `windows/p2s.ps1`: PowerShell client

Same approach as the app, as a script. Needs the patched `openvpn.exe` in `bin\` next to the script
(build it with `build/build.sh`, it ends up in `.build/out/bin/`) and OpenVPN 2.7's TAP driver
(`tapctl.exe`) installed.

```powershell
p2s import .\azurevpnconfig.xml dev     # add a profile
p2s up dev                              # sign in (browser) and connect, elevated window
p2s status                              # adapters, addresses, routes
p2s dns                                 # NRPT rules routing private domains to the VPN DNS
p2s down all
```

`New-P2SHostsList.ps1` builds a hosts-file list of private endpoint names from Azure Resource Graph
(needs the Azure CLI, signed in). Only needed when a VPN pushes no DNS server.

```powershell
.\New-P2SHostsList.ps1 -Tenant <tenant-guid> -Ranges '10.40.0.0/16','172.16.0.0/16' -Exclude '192.168.1.0/24'
p2s hosts apply
```

## `wsl/p2s-ns`: several OpenP2S connections in WSL

[OpenP2S](https://github.com/wyruweso/openp2s) allows one connection per network namespace. `p2s-ns`
gives each extra profile its own namespace, with NAT to the internet, so they can run side by side:

```bash
p2s-ns up dev            # profiles are read from ~/vpn/<name>.xml
p2s-ns up production
p2s-ns route dev         # make dev's ranges reachable from normal WSL
p2s-ns exec production ssh 192.168.0.4
p2s-ns proxy             # SOCKS5 on 127.0.0.1:1080 for Windows apps
p2s-ns status
p2s-ns down-all
```

Needs `sudo`, `nftables` and `openp2s`; `microsocks` for `proxy`.
