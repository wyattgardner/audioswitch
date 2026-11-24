# Headless Audio Over IP sender

This folder holds a small modification of [Audio Over IP](https://github.com/ElTheLedge/Audio-Over-IP) by ElTheLedge, kept as a patch against a pinned upstream release (see `UPSTREAM.json`).

**License:** the patch is a derivative of Audio Over IP and is licensed under the **EUPL-1.2**, the same as upstream (see `LICENSE` in this folder). The rest of this repository is MIT. No upstream source is stored here, only the patch.

## What the patch changes

- `main.go`: tagged `//go:build !headless`, so the GUI isn't compiled into the headless build.
- `main_headless.go` (new): starts the same capture and server code as the GUI's Sender mode at launch, with no window, tray icon or WebView2. Flag: `-port` (default 8080). The protocol is unchanged: `/getAudioConfig`, `/audio` websocket, `/info`. It also keeps one thread in COM's multithreaded apartment for the life of the process. The HTTP handlers call the Windows audio API from threads that never initialize COM, and without this they fail with HTTP 500. The GUI build gets that from WebView2.
- `wcatools/com.go`: `InitCOM` treats `CoInitialize` returning `S_FALSE` ("already initialized") as success instead of failure.

Logs go to `%APPDATA%\ElTheLedge\AudioOverIP\logs\headless-server.log`. The exe exits with code 1 if the server stops, so a scheduled task can restart it.

## Build

Needs Go 1.26 or later and git.

```powershell
./build-sender.ps1                # output in sender/bin
./build-sender.ps1 -OutDir C:\somewhere
```

## Upstream updates

`./check-upstream.ps1` looks up the latest upstream release and test-builds the patch against it. Exit codes: 0 up to date, 10 a new release builds cleanly, 20 the patch needs updating. `.github/workflows/upstream-check.yml` runs it weekly and opens an issue when a new release appears.
