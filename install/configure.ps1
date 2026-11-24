# Configures an installed AudioSwitch. Used by the installer and by setup.ps1 (source builds).
# Layout under -InstallDir: AudioSwitch.exe, NAudio.dll, sender\AudioOverIP-headless.exe, local\network.json.
#
#   configure.ps1 -PeerHost 192.0.2.20 [-Hotkey F13] [-Firewall]   write config, register logon tasks, start both
#   configure.ps1                                                   same, reusing the existing local\network.json
#   configure.ps1 -Stop                                             stop both cleanly and remove the tasks (before an upgrade)
#   configure.ps1 -Uninstall                                        -Stop, plus remove the firewall rules
param(
    [string]$InstallDir = $PSScriptRoot,
    [string]$PeerHost,
    [int]$Port = 8080,
    [int]$ControlPort = 8091,
    # Push hotkey for this machine, e.g. F13 or Ctrl+Alt+F9. Shift plus it becomes pull.
    [string]$Hotkey,
    [switch]$Firewall,
    [switch]$Stop,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$senderTask = 'Audio Over IP sender (headless)'
$switchTask = 'AudioSwitch'
$switchExe = Join-Path $InstallDir 'AudioSwitch.exe'
$senderExe = Join-Path $InstallDir 'sender\AudioOverIP-headless.exe'
$log = Join-Path $InstallDir 'install.log'

function Say($msg) {
    $msg
    try { Add-Content $log ("{0:s} {1}" -f (Get-Date), $msg) } catch { }
}

function Invoke-Elevated($command) {
    Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $command
}

function Stop-AudioSwitch {
    foreach ($t in $switchTask, $senderTask) {
        if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $t -Confirm:$false
            Say "Removed scheduled task '$t'"
        }
    }
    # Ask AudioSwitch to quit so it unmutes whatever it muted, then make sure.
    $running = Get-Process AudioSwitch -ErrorAction SilentlyContinue
    if ($running) {
        $exe = $running[0].Path
        if ($exe -and (Test-Path $exe)) { Start-Process $exe -ArgumentList '--quit' -Wait }
        for ($i = 0; $i -lt 20 -and (Get-Process AudioSwitch -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
        Get-Process AudioSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
        Say 'Stopped AudioSwitch'
    }
    if (Get-Process AudioOverIP-headless -ErrorAction SilentlyContinue) {
        Get-Process AudioOverIP-headless | Stop-Process -Force
        Say 'Stopped the headless sender'
    }
}

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
    Say "Registered and started scheduled task '$name'"
}

if ($Stop -or $Uninstall) {
    Stop-AudioSwitch
    if ($Uninstall -and (Get-NetFirewallRule -DisplayName 'AudioSwitch sender', 'AudioSwitch control' -ErrorAction SilentlyContinue)) {
        try {
            Invoke-Elevated "Remove-NetFirewallRule -DisplayName 'AudioSwitch sender','AudioSwitch control' -ErrorAction SilentlyContinue"
            Say 'Removed firewall rules'
        }
        catch { Say "Firewall rules left in place (admin declined): $($_.Exception.Message)" }
    }
    exit 0
}

if (-not (Test-Path $switchExe) -or -not (Test-Path $senderExe)) { throw "AudioSwitch isn't installed in $InstallDir" }

# ---- config ----
$localDir = Join-Path $InstallDir 'local'
$config = Join-Path $localDir 'network.json'
New-Item -ItemType Directory -Force $localDir | Out-Null
if ($PeerHost) {
    [ordered]@{
        peer        = [ordered]@{ host = $PeerHost; port = $Port; controlPort = $ControlPort }
        controlPort = $ControlPort
    } | ConvertTo-Json | Set-Content $config -Encoding ASCII
    Say "Peer: $PeerHost"
}
elseif (-not (Test-Path $config)) { throw 'No peer configured. Pass -PeerHost <the other machine''s IP or name>.' }
$cfg = Get-Content $config -Raw | ConvertFrom-Json

if ($Hotkey) {
    Add-Type -AssemblyName System.Windows.Forms
    $mod = 0; $key = $null
    foreach ($part in $Hotkey.Split('+') | ForEach-Object { $_.Trim() }) {
        switch -Regex ($part) {
            '^(ctrl|control)$' { $mod = $mod -bor 2 }
            '^alt$'            { $mod = $mod -bor 1 }
            '^shift$'          { $mod = $mod -bor 4 }
            '^win$'            { $mod = $mod -bor 8 }
            default            { $key = $part }
        }
    }
    $vk = [int][System.Windows.Forms.Keys]::Parse([System.Windows.Forms.Keys], $key, $true)
    Set-Content (Join-Path $InstallDir 'hotkey.txt') "$mod,$vk" -Encoding ASCII
    Say "Hotkey: $Hotkey"
}

# ---- playback device check ----
$render = @(Get-PnpDevice -Class AudioEndpoint -Status OK -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'SWD\MMDEVAPI\{0.0.0.00000000}*' })
if ($render.Count -eq 0) { Say 'WARNING: no active playback device. The sender has nothing to capture until one is connected or enabled.' }

# ---- firewall: inbound sender and control ports, from the peer only ----
if ($Firewall) {
    $addrs = @([System.Net.Dns]::GetHostAddresses($cfg.peer.host) | Where-Object AddressFamily -eq 'InterNetwork' | ForEach-Object { $_.ToString() })
    if ($addrs.Count -eq 0) { Say "WARNING: couldn't resolve $($cfg.peer.host); no firewall rules added." }
    else {
        $list = ($addrs | ForEach-Object { "'$_'" }) -join ','
        $cmd = "Remove-NetFirewallRule -DisplayName 'AudioSwitch sender','AudioSwitch control' -ErrorAction SilentlyContinue; " +
               "New-NetFirewallRule -DisplayName 'AudioSwitch sender' -Direction Inbound -Protocol TCP -LocalPort $($cfg.peer.port) -RemoteAddress $list -Action Allow | Out-Null; " +
               "New-NetFirewallRule -DisplayName 'AudioSwitch control' -Direction Inbound -Protocol TCP -LocalPort $($cfg.controlPort) -RemoteAddress $list -Action Allow | Out-Null"
        try { Invoke-Elevated $cmd; Say "Firewall: allowed TCP $($cfg.peer.port) and $($cfg.controlPort) inbound from $($addrs -join ', ')" }
        catch { Say "WARNING: firewall rules not added (admin declined). Command: $cmd" }
    }
}

Stop-AudioSwitch
Register-LogonTask $senderTask $senderExe "-port $($cfg.peer.port)"
Register-LogonTask $switchTask $switchExe $null
Say "Configured. AudioSwitch state: $InstallDir\state.txt"
