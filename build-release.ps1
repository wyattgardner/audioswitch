# Builds the release: AudioSwitch, the headless sender, all license files, and the Inno Setup installer.
# Output: dist\AudioSwitch-Setup-<version>.exe. Needs git, Go 1.26+ and Inno Setup 6.
param([string]$OutDir = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$version = (Get-Content (Join-Path $here 'VERSION') -Raw).Trim()

& (Join-Path $here 'build.ps1')

$stage = Join-Path $here 'obj\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, (Join-Path $stage 'licenses') | Out-Null
& (Join-Path $here 'sender\build-sender.ps1') -OutDir (Join-Path $stage 'sender') -LicensesDir (Join-Path $stage 'licenses\go')
if (-not (Test-Path (Join-Path $stage 'sender\AudioOverIP-headless.exe'))) { throw 'Sender build failed.' }

Copy-Item (Join-Path $here 'bin\AudioSwitch.exe'), (Join-Path $here 'bin\NAudio.dll'), (Join-Path $here 'install\configure.ps1') $stage
Copy-Item (Join-Path $here 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $here 'README.md'), (Join-Path $here 'THIRD_PARTY_NOTICES.md') $stage
Copy-Item (Join-Path $here 'lib\NAudio-LICENSE.txt') (Join-Path $stage 'licenses\NAudio-Ms-PL.txt')
Copy-Item (Join-Path $here 'sender\LICENSE') (Join-Path $stage 'licenses\AudioOverIP-EUPL-1.2.txt')

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required (jrsoftware.org/isinfo.php).' }

New-Item -ItemType Directory -Force $OutDir | Out-Null
& $iscc /Q "/DAppVersion=$version" "/DStage=$stage" "/O$OutDir" (Join-Path $here 'installer\AudioSwitch.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $OutDir "AudioSwitch-Setup-$version.exe"
"Built $setup"
"SHA-256 " + (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
