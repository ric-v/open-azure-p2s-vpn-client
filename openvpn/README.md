# OpenVPN for Azure Entra ID

Azure P2S gateways with Entra ID expect the Entra **access token as the OpenVPN password**
(username `AzureAD`). The token is about 2–3 KB; stock OpenVPN truncates it:

| Limit | Stock | Patched | Why |
|---|---|---|---|
| `USER_PASS_LEN` (`misc.h`) | 128 | 8192 | The password buffer. |
| `TLS_CHANNEL_BUF_SIZE` (`common.h`) | 2048 | 16384 | The key-method-2 control message that carries the password. |

[`long-credentials.patch`](long-credentials.patch) makes exactly those two changes. It is taken unmodified
from [OpenP2S](https://github.com/wyruweso/openp2s) (`patches/2.7.8/long-credentials.patch`,
GPL-2.0-only); see the comments in the patch. Related upstream work: Gerrit
[1593](https://gerrit.openvpn.net/c/openvpn/+/1593) (multi-line management passwords, merged in 2.7.2) and
[1622](https://gerrit.openvpn.net/c/openvpn/+/1622) (control-channel buffer).

[`build-openvpn-win.sh`](build-openvpn-win.sh) cross-compiles OpenSSL 3.5.9 (static) and the patched
OpenVPN 2.7.8 for Windows x64 with mingw-w64. It is called by [`../build/build.sh`](../build/build.sh),
which downloads and checksum-verifies the inputs first. The result is one `openvpn.exe` that depends only
on DLLs that ship with Windows 10/11.
