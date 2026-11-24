# Builds AudioSwitch.exe into .\bin. Downloads the pinned NAudio release from NuGet on first run.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$naudioVersion = '1.10.0'
$naudioSha256 = 'E4A80EED41DAD794695F9C114432DE4667D20BDFA80825AA94D159708CF29217'
$lib = Join-Path $here 'lib'
$naudio = Join-Path $lib 'NAudio.dll'

if (-not (Test-Path $naudio)) {
    New-Item -ItemType Directory -Force $lib | Out-Null
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("naudio-" + [guid]::NewGuid())
    New-Item -ItemType Directory $tmp | Out-Null
    $pkg = Join-Path $tmp "naudio.$naudioVersion.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/naudio/$naudioVersion/naudio.$naudioVersion.nupkg" -OutFile $pkg -UseBasicParsing
    $hash = (Get-FileHash $pkg -Algorithm SHA256).Hash
    if ($hash -ne $naudioSha256) { throw "NAudio package hash mismatch: $hash" }
    Expand-Archive $pkg (Join-Path $tmp 'x')
    Copy-Item (Join-Path $tmp 'x\lib\net35\NAudio.dll') $naudio
    Remove-Item $tmp -Recurse -Force
    "Downloaded NAudio $naudioVersion"
}

$bin = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$bin\AudioSwitch.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:"$naudio" `
    "$here\AudioSwitch.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item $naudio $bin -Force
"Built $bin\AudioSwitch.exe"
