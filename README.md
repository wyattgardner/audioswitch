# AudioSwitch

A tiny Windows tray app that switches which of two computers you hear through one set of speakers, using a single hotkey.

It pairs with [Scream](https://github.com/duncanthrax/scream), a virtual audio driver that streams a PC's audio over the LAN. Computer A (the one with the speakers) runs AudioSwitch and ScreamReader. Computer B runs the Scream driver and sends its audio to A.

## How it works

- AudioSwitch launches `ScreamReader.exe`, which plays Scream's network audio through A's default output.
- The hotkey toggles between two modes by muting and unmuting per-app sessions in the Windows mixer (Core Audio session API):
  - **PC mode:** ScreamReader is muted, everything else on A plays.
  - **Laptop mode:** every app except ScreamReader is muted. New apps are muted within a second.
- Tray icon: blue "PC" or orange "LT". Left-click toggles. Right-click has **Set hotkey...** and **Exit**.
- Exit unmutes everything it muted and closes ScreamReader.

No network code, no admin rights, no driver on A. One source file, no dependencies beyond the .NET Framework that ships with Windows.

## Build

```powershell
./build.ps1
```

This compiles `AudioSwitch.cs` with the built-in `csc.exe` into `AudioSwitch.exe`.

## Install

1. Download `Scream4.0.zip` from the [Scream releases](https://github.com/duncanthrax/scream/releases/tag/4.0).
2. Copy `Install\clients\Windows\ScreamReader\` (ScreamReader.exe and NAudio.dll) into a `ScreamReader` folder next to `AudioSwitch.exe`.
3. Run `AudioSwitch.exe`. Allow ScreamReader through Windows Firewall on private networks (UDP 4010).
4. On computer B, run the Scream driver installer (`Install-x64.bat`, as admin) and set **Speakers (Scream)** as its default output.
5. Default hotkey is Ctrl+Alt+F9. Use **Set hotkey...** in the tray menu to bind any key, for example an F13 to F24 key from a remapped keyboard. The choice is saved next to the exe in `hotkey.txt`.

If multicast is blocked on your network, set the Scream driver to unicast: add a string value `UnicastIPv4` with computer A's IP under `HKLM\SYSTEM\CurrentControlSet\Services\Scream\Options`, then disable and re-enable the Scream device.

## Notes

- Windows only. Tested on Windows 11.
- The exe is unsigned, so SmartScreen or antivirus may complain.
- ScreamReader plays through the default output device. Changing the default device while running works, but apps already muted on the old device stay muted until you switch modes.
- Only the audio session mute flag is touched. Volume levels are never changed.
