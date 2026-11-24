# AudioSwitch

A small Windows tray app that lets one set of speakers or headphones play either your PC or a second computer (say, a work laptop) on the same LAN, toggled with one hotkey.

The laptop runs [Audio Over IP](https://github.com/ElTheLedge/Audio-Over-IP) in Sender mode. It captures system audio with WASAPI loopback, so no driver is needed, which matters on locked-down work machines. The PC runs AudioSwitch, which has its own Audio Over IP receiver built in.

## How it works

- AudioSwitch connects to the sender (`GET /getAudioConfig`, then raw float32 PCM over the `/audio` websocket) and reconnects every few seconds if the laptop goes away.
- The hotkey toggles two modes:
  - **PC mode:** laptop audio is dropped and PC apps play normally.
  - **Laptop mode:** laptop audio plays and every other app on the PC's default output is muted in the Windows mixer. Apps started later are muted within a second.
- Latency is capped at about 150 ms. If the network backs up, the buffer is flushed instead of drifting.
- The tray icon is a blue "PC" or orange "LT". Left-click toggles. Right-click has **Set hotkey...** and **Exit**. The tooltip shows the connection status.
- Exit unmutes everything AudioSwitch muted.

AudioSwitch only touches session mute flags. It never changes volume levels.

## Setup

### Laptop (sender)

```powershell
./setup.ps1 -Role Laptop
```

This downloads the pinned Audio Over IP release (v0.9.0.1), checks its size and SHA-256, unpacks it to `%LOCALAPPDATA%\AudioOverIP` and adds it to sign-in startup. Open it, go to the **Sender** tab and press **Start**. It listens on TCP 8080. It doesn't remember being started, so this has to be done after every launch.

Audio Over IP is EUPL-1.2 licensed. This repo doesn't include or modify its code, it just downloads the official release.

### PC (receiver and switch)

1. Copy `network.example.json` to `local/network.json` and fill in the laptop's address. `local/` is git-ignored, so real addresses never get committed.
2. Run:

   ```powershell
   ./setup.ps1 -Role PC
   ```

   This downloads NAudio 1.10.0 from NuGet (hash pinned), builds `AudioSwitch.exe` with the C# compiler that ships with Windows, installs it to `%LOCALAPPDATA%\AudioSwitch`, adds it to sign-in startup and starts it.
3. The default hotkey is Ctrl+Alt+F9. **Set hotkey...** binds any key, for example an F13 to F24 key from a remapped keyboard. The choice is saved next to the exe in `hotkey.txt`.

To rebuild without installing, run `./build.ps1`. The output goes to `bin/`.

## Troubleshooting

- The tooltip or `%LOCALAPPDATA%\AudioSwitch\state.txt` shows the receiver status.
- "Actively refused": the sender isn't started on the laptop, or a firewall blocks TCP 8080. From the PC, check with `Test-NetConnection <laptop-ip> -Port 8080`.
- Connected but silent: the sender captures the laptop's default output device, so make sure the laptop plays to a real device.
- Give both machines DHCP reservations so the address in `local/network.json` doesn't change.

## Notes

- Windows only. Tested on Windows 11.
- The stream isn't encrypted. Keep it on a trusted LAN.
- AudioSwitch plays to the PC's default output device as it was when the connection started.
- The exe is unsigned, so SmartScreen or antivirus may complain.
