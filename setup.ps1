# Builds AudioSwitch and the headless sender from source and installs them, the same layout the installer uses.
# Most people should use the installer from the GitHub release instead. This needs git and Go 1.26+.
#
#   ./setup.ps1 -PeerHost 192.0.2.20 [-Hotkey F13]    first run
#   ./setup.ps1                                        later runs (reuses the installed config)
param(
    [string]$PeerHost,
    [int]$Port = 8080,
    [int]$ControlPort = 8091,
    [string]$Hotkey,
    [string]$InstallDir = (Join-Path $env:USERPROFILE 'Tools\AudioSwitch'),
    [switch]$SkipFirewall
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $here 'build.ps1')
$senderBuild = Join-Path $here 'sender\bin'
& (Join-Path $here 'sender\build-sender.ps1') -OutDir $senderBuild
if (-not (Test-Path (Join-Path $senderBuild 'AudioOverIP-headless.exe'))) { throw 'Sender build failed.' }

# Stop the running copies before replacing their files.
$installedConfigure = Join-Path $InstallDir 'configure.ps1'
if (Test-Path $installedConfigure) { & $installedConfigure -InstallDir $InstallDir -Stop }
else { & (Join-Path $here 'install\configure.ps1') -InstallDir $InstallDir -Stop }

New-Item -ItemType Directory -Force (Join-Path $InstallDir 'sender') | Out-Null
Copy-Item (Join-Path $here 'bin\AudioSwitch.exe'), (Join-Path $here 'bin\NAudio.dll'), (Join-Path $here 'install\configure.ps1') $InstallDir -Force
Copy-Item (Join-Path $senderBuild '*') (Join-Path $InstallDir 'sender') -Force

$configureArgs = @{ InstallDir = $InstallDir; Port = $Port; ControlPort = $ControlPort; Firewall = (-not $SkipFirewall) }
if ($PeerHost) { $configureArgs.PeerHost = $PeerHost }
if ($Hotkey) { $configureArgs.Hotkey = $Hotkey }
& (Join-Path $InstallDir 'configure.ps1') @configureArgs
