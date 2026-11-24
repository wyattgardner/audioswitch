# Installs both halves of AudioSwitch on this machine, the same on every machine:
#   - the headless Audio Over IP sender (shares this machine's audio on TCP -Port), and
#   - AudioSwitch (plays the peer's audio, hotkeys, peer control on TCP -ControlPort).
# Both go under %USERPROFILE%\Tools and start from per-user logon tasks. Not under %LOCALAPPDATA%: when Claude
# desktop runs this, that path is redirected into its MSIX package folder, where Task Scheduler can't see the files.
#
#   ./setup.ps1 -PeerHost 192.0.2.20     first run: writes local/network.json with the other machine's address
#   ./setup.ps1                          later runs: reuses local/network.json
param(
    [string]$PeerHost,
    [int]$Port = 8080,
    [int]$ControlPort = 8091,
    [switch]$SkipFirewall
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $env:USERPROFILE 'Tools'
$startup = [Environment]::GetFolderPath('Startup')

function Register-LogonTask($name, $exe, $arguments) {
    $action = if ($arguments) { New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory (Split-Path $exe) }
              else { New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe) }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $user
    # Task Scheduler's restart-on-failure only covers a failed launch, not a crash or a non-zero exit.
    # A once-a-minute repeat with IgnoreNew restarts the program within a minute and is a no-op while it runs.
    $watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 `
        -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $name -Action $action -Trigger @($logon, $watchdog) -Settings $settings -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    "Registered and started scheduled task '$name'"
}

# ---- config ----
$localDir = Join-Path $here 'local'
$config = Join-Path $localDir 'network.json'
if ($PeerHost) {
    New-Item -ItemType Directory -Force $localDir | Out-Null
    $cfg = [ordered]@{
        peer        = [ordered]@{ host = $PeerHost; port = $Port; controlPort = $ControlPort }
        controlPort = $ControlPort
    }
    $cfg | ConvertTo-Json | Set-Content $config -Encoding ASCII
    "Wrote $config (peer $PeerHost)"
}
elseif (-not (Test-Path $config)) {
    throw "No local/network.json yet. Run: ./setup.ps1 -PeerHost <other machine's IP>"
}
$cfg = Get-Content $config -Raw | ConvertFrom-Json
$peer = $cfg.peer.host

# ---- playback device check ----
# Loopback capture needs an active playback device: with none, Windows renders nothing and the sender answers 500.
$render = @(Get-PnpDevice -Class AudioEndpoint -Status OK -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'SWD\MMDEVAPI\{0.0.0.00000000}*' })
if ($render.Count -eq 0) {
    Write-Warning 'No active playback device on this machine. The sender has nothing to capture until one is connected or enabled.'
}
else { "Active playback devices: " + (($render | ForEach-Object FriendlyName) -join ', ') }

# ---- sender ----
$senderDir = Join-Path $tools 'AudioOverIP'
Get-Process AudioOverIP-headless -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
& (Join-Path $here 'sender\build-sender.ps1') -OutDir $senderDir
if (-not (Test-Path (Join-Path $senderDir 'AudioOverIP-headless.exe'))) { throw 'Sender build failed.' }
Remove-Item (Join-Path $startup 'AudioOverIP.lnk') -ErrorAction SilentlyContinue

# ---- AudioSwitch ----
& (Join-Path $here 'build.ps1')
$switchDir = Join-Path $tools 'AudioSwitch'
Get-Process AudioSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force (Join-Path $switchDir 'local') | Out-Null
Copy-Item (Join-Path $here 'bin\AudioSwitch.exe'), (Join-Path $here 'bin\NAudio.dll') $switchDir -Force
Copy-Item $config (Join-Path $switchDir 'local\network.json') -Force
$oldHotkey = Join-Path $env:LOCALAPPDATA 'AudioSwitch\hotkey.txt'
if ((Test-Path $oldHotkey) -and -not (Test-Path (Join-Path $switchDir 'hotkey.txt'))) { Copy-Item $oldHotkey $switchDir }
Remove-Item (Join-Path $startup 'AudioSwitch.lnk') -ErrorAction SilentlyContinue

# ---- firewall: inbound sender and control ports, from the peer only ----
if (-not $SkipFirewall) {
    $addrs = @([System.Net.Dns]::GetHostAddresses($peer) | Where-Object AddressFamily -eq 'InterNetwork' | ForEach-Object { $_.ToString() })
    if ($addrs.Count -eq 0) { Write-Warning "Couldn't resolve $peer, so no firewall rules were added." }
    else {
        $list = ($addrs | ForEach-Object { "'$_'" }) -join ','
        $cmd = "Remove-NetFirewallRule -DisplayName 'AudioSwitch sender','AudioSwitch control' -ErrorAction SilentlyContinue; " +
               "New-NetFirewallRule -DisplayName 'AudioSwitch sender' -Direction Inbound -Protocol TCP -LocalPort $($cfg.peer.port) -RemoteAddress $list -Action Allow | Out-Null; " +
               "New-NetFirewallRule -DisplayName 'AudioSwitch control' -Direction Inbound -Protocol TCP -LocalPort $($cfg.controlPort) -RemoteAddress $list -Action Allow | Out-Null"
        try {
            Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $cmd
            "Firewall: allowed TCP $($cfg.peer.port) and $($cfg.controlPort) inbound from $($addrs -join ', ')"
        }
        catch { Write-Warning "Firewall rules not added (admin declined). Run setup again as admin, or add them by hand: $cmd" }
    }
}

Register-LogonTask 'Audio Over IP sender (headless)' (Join-Path $senderDir 'AudioOverIP-headless.exe') "-port $Port"
Register-LogonTask 'AudioSwitch' (Join-Path $switchDir 'AudioSwitch.exe') $null
"Done. Sender log: %APPDATA%\ElTheLedge\AudioOverIP\logs\headless-server.log  AudioSwitch state: $switchDir\state.txt"
