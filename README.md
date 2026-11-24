# AudioSwitch

Send audio between two Windows machines on the same LAN, in either direction, with one hotkey. For example, a personal PC and a work laptop, each with its own speakers: press F13 on either one to play its audio through the other's speakers instead.

Each machine runs the same two pieces:

- a headless build of [Audio Over IP](https://github.com/ElTheLedge/Audio-Over-IP) that shares the machine's system audio over the LAN. It uses WASAPI loopback, so no driver is needed, which matters on locked-down work machines.
- AudioSwitch, a tray app that plays the other machine's audio, handles the hotkeys, and coordinates with the other AudioSwitch.

Both start on their own at sign-in.

## Switching

Press **F13 on the machine whose audio you want to move.** Its audio then plays on the other machine's speakers. Press F13 on it again to bring the audio back.

**Shift+F13** pulls the other machine's audio to the one you're on, so you don't have to switch keyboards or a KVM to reach it. Press it again to send that audio back.

With one keyboard on a KVM, the key goes to whichever machine the KVM has selected. So F13 moves that machine's audio, and Shift+F13 brings the other one's here.

| Key | Effect, on the machine that receives the keypress |
| --- | --- |
| **F13** | Send this machine's audio to the other machine, or play it here again |
| **Shift+F13** | Ask the other machine to send its audio here, or to take it back |

- Each machine's speakers play exactly one source:
  - **Sending:** the machine's own speakers are muted at the endpoint. Loopback capture is taken before endpoint mute, so the stream stays at full level.
  - **Receiving:** every other app on the machine's default output is muted, polled every second so apps that start later are caught too, and only the other machine's stream plays. Exactly those sessions are restored when it stops receiving. What was muted is recorded in `rx-muted.txt`, so a crash or restart restores it on the next start. Sessions you had already muted yourself are never touched.
- Only one machine sends at a time. If the other one starts sending, this one goes back to playing locally. Two senders would loop each other's audio back and forth.
- Sending only starts if the other AudioSwitch answers and is connected to this machine's sender. Otherwise you get a balloon and the audio stays put. If the other machine disappears while you're sending, this machine goes back to local after about 15 seconds.
- Tray icon: blue **LO** (playing locally), orange **TX** (sending), green **RX** (playing the other machine's audio). Left-click = F13. Right-click has the same actions, **Preferences** and **Exit**.
- **Preferences > Show notifications** turns Windows notifications for switches and errors on or off. It's off by default and saved per machine in `prefs.txt`. **Preferences > Set hotkey...** binds any key (Shift plus that key becomes pull). Its own prompts always show.
- Exit, or a crash followed by restart, unmutes anything AudioSwitch muted.

## Install (on each machine)

1. Download `AudioSwitch-Setup-x.y.z.exe` from the [latest release](https://github.com/wyattgardner/audioswitch/releases/latest). If you like, check it against `SHA256SUMS.txt` from the same release:

   ```powershell
   (Get-FileHash .\AudioSwitch-Setup-1.0.0.exe -Algorithm SHA256).Hash
   ```

2. Run it on **both** machines. It installs per-user, with no admin rights needed. It asks for:
   - **The other machine's IP address or name.** Give both machines DHCP reservations so the address doesn't change.
   - **This machine's hotkey.** Default F13. Shift plus the key becomes pull. Use something like `Ctrl+Alt+F9` if the keyboard has no F13.
   - **Windows Firewall:** whether to allow the other machine in on TCP 8080 and 8091. The rules are scoped to the other machine's address. This step asks for admin once. Untick it if the firewall is managed elsewhere.

That's it. Both programs start right away and at every sign-in, and the tray icon shows blue **LO**. The exe isn't signed yet, so Windows SmartScreen may warn on first run ("More info", then "Run anyway").

What the installer does:

- Installs to `%USERPROFILE%\Tools\AudioSwitch`: `AudioSwitch.exe`, `NAudio.dll`, `sender\AudioOverIP-headless.exe`, `configure.ps1`, and license files. It's not under `%LOCALAPPDATA%`, because when Claude desktop runs an installer `%LOCALAPPDATA%` is redirected into its own package folder, out of Task Scheduler's sight.
- Writes `local\network.json` (the other machine's address) and `hotkey.txt` there.
- Registers two per-user logon tasks, "AudioSwitch" and "Audio Over IP sender (headless)". A once-a-minute repeat with "ignore new instance" restarts either one within a minute if it dies.
- Logs to `install.log` in the install folder.

**Upgrade:** run the newer installer. It asks AudioSwitch to quit cleanly, which restores any mutes, replaces the files, keeps your peer and hotkey, and restarts everything.

**Uninstall:** Settings > Apps > AudioSwitch. It quits AudioSwitch cleanly, removes both logon tasks and the firewall rules (asks for admin), and deletes the install folder.

**Silent install**, for scripts or other admins:

```powershell
.\AudioSwitch-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /PeerHost=192.0.2.20 /Hotkey=F13 [/NoFirewall]
```

On an upgrade, `/PeerHost` and `/Hotkey` can be left out, and the previous answers are reused.

### From source

Needs git, Go 1.26 or later, and Windows' built-in .NET Framework 4.x.

```powershell
git clone https://github.com/wyattgardner/audioswitch.git
cd audioswitch
./setup.ps1 -PeerHost <the other machine's IP> [-Hotkey F13] [-SkipFirewall]
```

This builds both programs (NAudio 1.10.0 from NuGet, hash-pinned; Audio Over IP v0.9.0.1, commit-pinned, plus `sender/headless.patch`) and installs them in the same layout as the installer. Later runs can drop `-PeerHost`.

To build the installer itself, you also need [Inno Setup 6](https://jrsoftware.org/isinfo.php): `./build-release.ps1` writes `dist\AudioSwitch-Setup-<version>.exe` and `dist\SHA256SUMS.txt`. Pushing a `v*` tag that matches `VERSION` makes the Release workflow build it on GitHub and publish the release.

## Checking it works

- **AudioSwitch state:** `%USERPROFILE%\Tools\AudioSwitch\state.txt`, refreshed every second. You want `receiver=[connected ...]` with `bytes=` growing, and `peer-control=[ok]` once the other machine is set up. `mode=` shows LOCAL, SENDING or RECEIVING.
- **AudioSwitch event log:** `%USERPROFILE%\Tools\AudioSwitch\events.log`. It records every hotkey press, control message in and out, feed and mode change, and suppressed notification, with millisecond timestamps. Compare both machines' logs to see who asked what.
- **Sender log:** `%APPDATA%\ElTheLedge\AudioOverIP\logs\headless-server.log`. It logs the playback device it captures, each client that connects, and "no active playback device" if there's nothing to capture.
- **From the other machine:** `Test-NetConnection <ip> -Port 8080` and `-Port 8091` should both succeed.

| Symptom | Likely cause |
| --- | --- |
| `receiver=[offline: ... actively refused]` | The other machine's sender isn't running, or its firewall blocks 8080 |
| `receiver=[sender error 500 ...]` | The other machine has no active playback device |
| `peer-control=[unreachable]` | The other AudioSwitch isn't running, or its firewall blocks 8091 |
| Connected but silent | The sending machine plays to a device other than its default, or nothing is playing |

## Managed machines and EDR

On a company-managed machine, endpoint security such as SentinelOne may quarantine or delete `AudioSwitch.exe` or the headless sender. They're unsigned, they register a global hotkey, they change audio mute state, and they listen on the network. The fix is an allow-list entry from IT. Don't rename, repack or relocate the files to get past it.

What to tell IT about each file:

- **AudioSwitch.exe** (built from `AudioSwitch.cs` in this repo, C#/.NET Framework 4, NAudio 1.10.0)
  - Per-user tray app started by a logon task, with no admin rights and no service.
  - Registers one global hotkey (default F13, plus Shift+F13) through `RegisterHotKey`. A low-level keyboard hook runs only while the user picks a new hotkey in the menu, for up to 20 s.
  - Connects to the other machine's sender on TCP 8080 (HTTP plus websocket) and plays that audio with NAudio/WASAPI.
  - Listens on TCP 8091 for one-line commands (`SENDING 1|0`, `TOGGLE`) and rejects any connection that isn't from the configured peer address. It also connects to the peer's 8091.
  - While sending: mutes this machine's default output endpoint. While receiving: mutes other apps' audio sessions on the default output, and restores them afterwards.
  - Writes only beside itself in `%USERPROFILE%\Tools\AudioSwitch`: state, event log, preferences and hotkey files.
- **AudioOverIP-headless.exe** (built from upstream Audio Over IP v0.9.0.1 plus `sender/headless.patch`, Go)
  - Captures the default playback device with WASAPI loopback and serves it on TCP 8080 to clients on the LAN. No window and no admin rights.
  - Logs to `%APPDATA%\ElTheLedge\AudioOverIP\logs`.

Neither one contacts the internet. The release exes are built by the public GitHub Actions Release workflow from the tagged source, so each release has fixed file hashes IT can allow-list. The installer's hash is in the release's `SHA256SUMS.txt`. A build from source makes different hashes, so allow-list by path in that case: `%USERPROFILE%\Tools\AudioSwitch\AudioSwitch.exe` and `...\sender\AudioOverIP-headless.exe`.

## Layout

| Path | What | License |
| --- | --- | --- |
| `AudioSwitch.cs`, `build.ps1` | Tray app and its build | MIT |
| `install/configure.ps1` | Writes config and hotkey, firewall rules, logon tasks, clean stop and uninstall. Shared by the installer and `setup.ps1`. | MIT |
| `installer/AudioSwitch.iss`, `build-release.ps1` | Inno Setup installer and the release build | MIT |
| `setup.ps1` | Build from source and install | MIT |
| `sender/` | Patch adding a headless sender to Audio Over IP, plus build and upstream-check scripts. See [sender/README.md](sender/README.md). | EUPL-1.2 |
| `VERSION`, `CHANGELOG.md` | Release version (stamped into the exe and installer) and release notes | |
| `network.example.json` | Shape of `local\network.json` | MIT |

Third-party components and their licenses are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Audio Over IP is by ElTheLedge, licensed EUPL-1.2, and NAudio 1.10.0 is Ms-PL. The repo stores only the Audio Over IP patch, never upstream source. Release installers include the built sender with its license and the license of every Go module it links.

## Upstream updates

`sender/check-upstream.ps1` test-builds the patch against the newest Audio Over IP release. A weekly GitHub Action runs it and opens an issue when a new release shows up.

## Notes

- Windows only. Tested on Windows 11.
- The stream and the control channel aren't encrypted or authenticated beyond "comes from the configured peer address". Keep it on a trusted LAN.
- Received audio plays on the receiving machine's default output device as it was when the connection started.
- Quitting from the tray lasts until the watchdog restarts it, within a minute. To stop it for good, disable the scheduled tasks or uninstall.
- The tray menu's first line shows the installed version, and `state.txt` starts with `version=`.
- The exes are unsigned, so SmartScreen or antivirus may complain.
