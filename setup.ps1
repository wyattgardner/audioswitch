# Sets up one side of AudioSwitch.
#   -Role PC      builds AudioSwitch, installs it to %LOCALAPPDATA%\AudioSwitch and starts it at sign-in.
#   -Role Laptop  downloads the pinned Audio Over IP release, verifies it and unpacks it to %LOCALAPPDATA%\AudioOverIP.
param(
    [Parameter(Mandatory)][ValidateSet('PC', 'Laptop')][string]$Role,
    [switch]$NoStartup
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$startup = [Environment]::GetFolderPath('Startup')

function New-Shortcut($path, $target) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
    $s.TargetPath = $target
    $s.WorkingDirectory = Split-Path -Parent $target
    $s.Save()
}

if ($Role -eq 'PC') {
    $config = Join-Path $here 'local\network.json'
    if (-not (Test-Path $config)) {
        New-Item -ItemType Directory -Force (Join-Path $here 'local') | Out-Null
        Copy-Item (Join-Path $here 'network.example.json') $config
        throw "Created $config from the example. Fill in the laptop's address, then run setup again."
    }

    & (Join-Path $here 'build.ps1')

    $dest = Join-Path $env:LOCALAPPDATA 'AudioSwitch'
    Get-Process AudioSwitch -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest\*" } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force (Join-Path $dest 'local') | Out-Null
    Copy-Item (Join-Path $here 'bin\AudioSwitch.exe'), (Join-Path $here 'bin\NAudio.dll') $dest -Force
    Copy-Item $config (Join-Path $dest 'local\network.json') -Force

    if (-not $NoStartup) { New-Shortcut (Join-Path $startup 'AudioSwitch.lnk') (Join-Path $dest 'AudioSwitch.exe') }
    Start-Process (Join-Path $dest 'AudioSwitch.exe') -WorkingDirectory $dest
    "Installed and started $dest\AudioSwitch.exe"
}
else {
    $version = 'v0.9.0.1'
    $size = 5601036
    $sha256 = '10C1A2D3A55344EBE89CBFCD7CB2C4B140477C7706A7F9BB2CB44814AA022E1B'
    $url = "https://github.com/ElTheLedge/Audio-Over-IP/releases/download/$version/AudioOverIP_release-Windows_x64.zip"

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("aoip-" + [guid]::NewGuid())
    New-Item -ItemType Directory $tmp | Out-Null
    $zip = Join-Path $tmp 'AudioOverIP.zip'
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $len = (Get-Item $zip).Length
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($len -ne $size -or $hash -ne $sha256) { throw "Audio Over IP download mismatch: size $len, SHA-256 $hash" }

    $dest = Join-Path $env:LOCALAPPDATA 'AudioOverIP'
    Expand-Archive $zip $dest -Force
    Remove-Item $tmp -Recurse -Force

    if (-not $NoStartup) { New-Shortcut (Join-Path $startup 'AudioOverIP.lnk') (Join-Path $dest 'AudioOverIP.exe') }
    "Unpacked Audio Over IP $version to $dest"
    "Open it, go to the Sender tab and press Start. It listens on TCP 8080 and has to be started after every launch."
}
