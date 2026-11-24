# Sets up one side of AudioSwitch. Both roles install under %USERPROFILE%\Tools and start from a per-user logon task.
#   -Role PC      builds AudioSwitch (switch + receiver) and installs it to %USERPROFILE%\Tools\AudioSwitch.
#   -Role Laptop  builds the headless Audio Over IP sender from sender/ and installs it to %USERPROFILE%\Tools\AudioOverIP.
# Not under %LOCALAPPDATA%: when Claude desktop runs this, that path is redirected into its MSIX package folder,
# where Task Scheduler can't see the files.
param(
    [Parameter(Mandatory)][ValidateSet('PC', 'Laptop')][string]$Role,
    [int]$Port = 8080
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
    $trigger = @($logon, $watchdog)
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 `
        -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    "Registered and started scheduled task '$name'"
}

if ($Role -eq 'PC') {
    $config = Join-Path $here 'local\network.json'
    if (-not (Test-Path $config)) {
        New-Item -ItemType Directory -Force (Join-Path $here 'local') | Out-Null
        Copy-Item (Join-Path $here 'network.example.json') $config
        throw "Created $config from the example. Fill in the laptop's address, then run setup again."
    }

    & (Join-Path $here 'build.ps1')

    $dest = Join-Path $tools 'AudioSwitch'
    Get-Process AudioSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force (Join-Path $dest 'local') | Out-Null
    Copy-Item (Join-Path $here 'bin\AudioSwitch.exe'), (Join-Path $here 'bin\NAudio.dll') $dest -Force
    Copy-Item $config (Join-Path $dest 'local\network.json') -Force

    $oldHotkey = Join-Path $env:LOCALAPPDATA 'AudioSwitch\hotkey.txt'
    if ((Test-Path $oldHotkey) -and -not (Test-Path (Join-Path $dest 'hotkey.txt'))) { Copy-Item $oldHotkey $dest }
    Remove-Item (Join-Path $startup 'AudioSwitch.lnk') -ErrorAction SilentlyContinue

    Register-LogonTask 'AudioSwitch' (Join-Path $dest 'AudioSwitch.exe') $null
}
else {
    # Loopback capture needs an active playback device: with none, Windows renders nothing and the sender answers 500.
    $render = @(Get-PnpDevice -Class AudioEndpoint -Status OK -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like 'SWD\MMDEVAPI\{0.0.0.00000000}*' })
    if ($render.Count -eq 0) {
        Write-Warning 'No active playback device on this machine. The sender has nothing to capture until one is connected or enabled (built-in speaker, or a plug in the dock''s audio jack).'
    }
    else { "Active playback devices: " + (($render | ForEach-Object FriendlyName) -join ', ') }

    $dest = Join-Path $tools 'AudioOverIP'
    Get-Process AudioOverIP-headless -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    & (Join-Path $here 'sender\build-sender.ps1') -OutDir $dest
    if (-not (Test-Path (Join-Path $dest 'AudioOverIP-headless.exe'))) { throw 'Sender build failed.' }

    Remove-Item (Join-Path $startup 'AudioOverIP.lnk') -ErrorAction SilentlyContinue
    Register-LogonTask 'Audio Over IP sender (headless)' (Join-Path $dest 'AudioOverIP-headless.exe') "-port $Port"
    "The sender listens on TCP $Port. Logs: %APPDATA%\ElTheLedge\AudioOverIP\logs\headless-server.log"
}
