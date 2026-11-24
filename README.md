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

Needs git, Go 1.26 or later, and Windows' built-in .NET Framework 4.x.

```powershell
git clone https://github.com/wyattgardner/audioswitch.git
cd audioswitch
./setup.ps1 -PeerHost <the other machine's IP>
```

Setup does the following:

1. Writes `local/network.json` with the other machine's address. `local/` is git-ignored, so real addresses never get committed. Later runs reuse it, so plain `./setup.ps1` is enough.
2. Builds the headless sender from the pinned upstream release plus `sender/headless.patch`, and installs it to `%USERPROFILE%\Tools\AudioOverIP`.
3. Builds AudioSwitch (NAudio 1.10.0 from NuGet, hash pinned) and installs it to `%USERPROFILE%\Tools\AudioSwitch`.
4. Adds inbound firewall rules for TCP 8080 (sender) and 8091 (AudioSwitch control), from the other machine's address only. This step asks for admin. If it's declined, setup prints the command to run.
5. Registers two per-user logon tasks, "Audio Over IP sender (headless)" and "AudioSwitch". A once-a-minute repeat with "ignore new instance" restarts either one within a minute if it dies.

Everything installs under `%USERPROFILE%\Tools`, not `%LOCALAPPDATA%`, because when Claude desktop runs a script `%LOCALAPPDATA%` is redirected into its own package folder, out of Task Scheduler's sight.

Options: `-Hotkey` (this machine's push key, e.g. `F13` or `Ctrl+Alt+F9`; Shift plus it becomes pull; default F13), `-Port` (sender, default 8080), `-ControlPort` (default 8091), `-SkipFirewall`. Use `-Hotkey` on a machine whose keyboard has no F13. Left-clicking the tray icon always works as a fallback.

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

Neither one contacts the internet. The only downloads are at build time: NuGet (NAudio, hash-pinned), GitHub (Audio Over IP source, commit-pinned) and the Go module proxy. Both exes are built locally from source by `setup.ps1`, so their hashes change with each build. An allow-list by path or by publisher (once signed) holds up better than one by hash.

## Layout

| Path | What | License |
| --- | --- | --- |
| `AudioSwitch.cs`, `build.ps1`, `setup.ps1` | Tray app, build and setup scripts | MIT |
| `sender/` | Patch adding a headless sender to Audio Over IP, plus build and upstream-check scripts. See [sender/README.md](sender/README.md). | EUPL-1.2 |
| `network.example.json` | Shape of `local/network.json` | MIT |

Audio Over IP is by ElTheLedge and licensed EUPL-1.2. This repo stores only the patch, never upstream source or binaries.

## Upstream updates

`sender/check-upstream.ps1` test-builds the patch against the newest Audio Over IP release. A weekly GitHub Action runs it and opens an issue when a new release shows up.

## Notes

- Windows only. Tested on Windows 11.
- The stream and the control channel aren't encrypted or authenticated beyond "comes from the configured peer address". Keep it on a trusted LAN.
- Received audio plays on the receiving machine's default output device as it was when the connection started.
- Quitting from the tray lasts until the watchdog restarts it, within a minute. To stop it for good, disable the scheduled tasks.
- The exes are unsigned, so SmartScreen or antivirus may complain.
