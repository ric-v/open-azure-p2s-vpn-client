# Third-party notices

Release builds of `P2SVpn.exe` contain or install the following components. Their exact versions and
download checksums are pinned in [`build/versions.env`](build/versions.env).

| Component | Version | License | How it is used |
|---|---|---|---|
| [OpenVPN](https://github.com/OpenVPN/openvpn) | 2.7.8 + `openvpn/long-credentials.patch` | GPL-2.0 with OpenSSL linking exception | Compiled from the upstream source tarball and embedded; extracted to `Program Files\P2SVpn\bin\openvpn.exe` and run by the service. |
| `openvpn/long-credentials.patch` | from OpenP2S 0.3.0 | GPL-2.0-only | Applied to OpenVPN before building. Copyright Mykhailo Tseluiko and OpenP2S contributors. |
| [OpenSSL](https://github.com/openssl/openssl) | 3.5.9 | Apache-2.0 | Statically linked into `openvpn.exe`. |
| [OpenVPN Community installer](https://openvpn.net/community-downloads/) | 2.7.8-I002 (signed by OpenVPN Inc.) | GPL-2.0 (components under their own licenses) | Embedded unmodified; run only to install the TAP-Windows6 driver and `tapctl.exe` when they are missing. |
| [TAP-Windows6](https://github.com/OpenVPN/tap-windows6) header `tap-windows.h` | master (SHA-256 pinned) | GPL-2.0 / see upstream | Used at build time only. |
| [.NET runtime and Windows Forms](https://github.com/dotnet) | 10.0 | MIT | Bundled in the self-contained exe. |
| System.ServiceProcess.ServiceController, System.Security.Cryptography.ProtectedData | 10.0 | MIT | NuGet packages. |

## Source for GPL components

The complete corresponding source for the bundled OpenVPN binary is the OpenVPN 2.7.8 source tarball
at the URL and SHA-256 in `build/versions.env`, plus `openvpn/long-credentials.patch`, built with
`openvpn/build-openvpn-win.sh` (configure options are in that script). OpenSSL 3.5.9 source is likewise
pinned there. `build/build.sh` rebuilds the binary from these inputs.

## Prior work

The approach of sending the Entra access token as the OpenVPN password, and the OpenVPN patch, come
from [OpenP2S](https://github.com/wyruweso/openp2s), which in turn credits
[cveld/azure-vpn-client-headless-container](https://github.com/cveld/azure-vpn-client-headless-container)
and Luca Boccassi's OpenVPN patch series.
