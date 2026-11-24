# Changelog

## [1.0.0] - 2026-11-24

First release.

- Two-way audio switching between two Windows machines on one LAN. F13 sends this machine's audio to the other machine, and Shift+F13 pulls the other machine's audio here. The hotkey is configurable per machine.
- Strict one-source switching: the sending machine mutes its own output endpoint, which loopback capture ignores, and the receiving machine mutes every other app until it stops receiving. Mutes are restored on exit or on the next start after a crash. Apps you muted yourself are never touched.
- Only one machine sends at a time, and a sender falls back to playing locally if the other machine stops answering for about 15 s.
- Headless Audio Over IP sender: v0.9.0.1 plus a patch, which adds a windowless build, a COM fix for `/getAudioConfig`, the `S_FALSE` fix and playback-device logging.
- Windows installer (per-user, no admin except for the optional firewall rules), with logon tasks plus a one-minute watchdog for both programs, and a clean shutdown on upgrade and uninstall.
- Tray icon with LO/TX/RX states, Preferences (notifications off by default, Set hotkey...), `state.txt` and `events.log` for troubleshooting.

Known gaps: PC to laptop and the 15 s peer-loss fallback haven't been tested between two real machines yet. Laptop to PC and crash recovery have.
