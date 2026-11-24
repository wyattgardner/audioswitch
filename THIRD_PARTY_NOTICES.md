# Third-party notices

AudioSwitch itself is MIT licensed (see LICENSE). The installer also ships these components, each under its own license. The full texts are in the `licenses` folder of an installed copy.

## Audio Over IP (headless sender)

- `sender\AudioOverIP-headless.exe` is built from [Audio Over IP](https://github.com/ElTheLedge/Audio-Over-IP) v0.9.0.1 (commit a0e2e93) by ElTheLedge, plus the modifications in `sender/headless.patch` in the AudioSwitch repository.
- License: **EUPL-1.2** (`licenses\AudioOverIP-EUPL-1.2.txt`). The modified source is the upstream tag plus that patch, both public. The exe is built from them by `sender/build-sender.ps1`.
- The exe also links Go modules and the Go runtime. Their licenses are in `licenses\go\`, one file per module.

## NAudio 1.10.0

- `NAudio.dll` is the unmodified NAudio 1.10.0 from NuGet, by Mark Heath and contributors. Source: https://github.com/naudio/NAudio/tree/v1.10.0
- License: **Microsoft Public License (Ms-PL)** (`licenses\NAudio-Ms-PL.txt`). NAudio moved to MIT in 2.x. Version 1.10.0 is Ms-PL, and this binary is distributed under it.
