# AudioSwitch

A small Windows tray app that lets one set of speakers or headphones play either your PC or a second computer (say, a work laptop) on the same LAN, toggled with one hotkey.

The laptop runs a headless build of [Audio Over IP](https://github.com/ElTheLedge/Audio-Over-IP) as the sender. It captures system audio with WASAPI loopback, so no driver is needed, which matters on locked-down work machines. The PC runs AudioSwitch, which has its own Audio Over IP receiver built in. Both start on their own at sign-in.

## How it works

- AudioSwitch connects to the sender (`GET /getAudioConfig`, then raw float32 PCM over the `/audio` websocket) and reconnects every few seconds if the laptop goes away.
- The hotkey toggles two modes:
  - **PC mode:** laptop audio is dropped and PC apps play normally.
  - **Laptop mode:** laptop audio plays and every other app on the PC's default output is muted in the Windows mixer. Apps started later are muted within a second.
- Latency is capped at about 150 ms. If the network backs up, the buffer is flushed instead of drifting.
- The tray icon is a blue "PC" or orange "LT". Left-click toggles. Right-click has **Set hotkey...** and **Exit**. The tooltip shows the connection status.
- Exit unmutes everything AudioSwitch muted.

AudioSwitch only touches session mute flags. It never changes volume levels.

## Layout

| Path | What | License |
| --- | --- | --- |
| `AudioSwitch.cs`, `build.ps1`, `setup.ps1` | Tray app with receiver, build and setup scripts | MIT |
| `sender/` | Patch that adds a headless sender to Audio Over IP, plus build and upstream-check scripts. See [sender/README.md](sender/README.md). | EUPL-1.2 |
| `network.example.json` | Template for `local/network.json` (git-ignored) | MIT |

## Setup

Both roles install under `%USERPROFILE%\Tools` and register a per-user scheduled task. It starts the program at sign-in and, via a once-a-minute repeat that does nothing while the program runs, restarts it within a minute if it dies. The install isn't under `%LOCALAPPDATA%`, because when Claude desktop runs a script that path is redirected into its own package folder, out of Task Scheduler's sight.

### Laptop (sender)

Needs Go 1.26 or later and git.

```powershell
./setup.ps1 -Role Laptop          # optional: -Port 8080
```

This builds `AudioOverIP-headless.exe` from the pinned upstream release plus `sender/headless.patch`, installs it with its EUPL license to `%USERPROFILE%\Tools\AudioOverIP`, removes any old GUI startup shortcut, and registers the task "Audio Over IP sender (headless)". Logs go to `%APPDATA%\ElTheLedge\AudioOverIP\logs\headless-server.log`.

### PC (receiver and switch)

1. Copy `network.example.json` to `local/network.json` and fill in the laptop's address. `local/` is git-ignored, so real addresses never get committed.
2. Run:

   ```powershell
   ./setup.ps1 -Role PC
   ```

   This downloads NAudio 1.10.0 from NuGet (hash pinned), builds `AudioSwitch.exe` with the C# compiler that ships with Windows, installs it to `%USERPROFILE%\Tools\AudioSwitch` and registers the task "AudioSwitch".
3. The default hotkey is Ctrl+Alt+F9. **Set hotkey...** binds any key, for example an F13 to F24 key from a remapped keyboard. The choice is saved next to the exe in `hotkey.txt`.

To rebuild without installing, run `./build.ps1` (output in `bin/`) or `./sender/build-sender.ps1` (output in `sender/bin/`).

## Upstream updates

`sender/check-upstream.ps1` test-builds the patch against the newest Audio Over IP release. A weekly GitHub Action (`.github/workflows/upstream-check.yml`) runs it and opens an issue when a new release shows up.

## Troubleshooting

- The tooltip or `%USERPROFILE%\Tools\AudioSwitch\state.txt` shows the receiver status.
- "Actively refused": the sender isn't running on the laptop. Check the task and its log. From the PC: `Test-NetConnection <laptop-ip> -Port 8080`.
- HTTP 500 from `/getAudioConfig`: the sender was built without the COM fix in `sender/headless.patch`.
- Connected but silent: the sender captures the laptop's default output device, so make sure the laptop plays to a real device.
- Give both machines DHCP reservations so the address in `local/network.json` doesn't change.

## Notes

- Windows only. Tested on Windows 11.
- The stream isn't encrypted. Keep it on a trusted LAN.
- AudioSwitch plays to the PC's default output device as it was when the connection started.
- Quitting from the tray menu lasts until the watchdog restarts it, within a minute. To stop it for good, disable the scheduled task.
- The exe is unsigned, so SmartScreen or antivirus may complain.
