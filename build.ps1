# Builds AudioSwitch.exe into .\bin. Downloads the pinned NAudio release (and its license) on first run.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = (Get-Content (Join-Path $here 'VERSION') -Raw).Trim()

$naudioVersion = '1.10.0'
$naudioSha256 = 'E4A80EED41DAD794695F9C114432DE4667D20BDFA80825AA94D159708CF29217'
# NAudio 1.x is Ms-PL (2.x moved to MIT). Its license text ships with the binary.
$naudioLicenseUrl = 'https://raw.githubusercontent.com/naudio/NAudio/v1.10.0/license.txt'
$naudioLicenseSha256 = '0027E744E6750BC02408B90C6909A7A33093477083D59A7E2660477E05307689'
$lib = Join-Path $here 'lib'
$naudio = Join-Path $lib 'NAudio.dll'
$naudioLicense = Join-Path $lib 'NAudio-LICENSE.txt'

New-Item -ItemType Directory -Force $lib | Out-Null
if (-not (Test-Path $naudio)) {
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
if (-not (Test-Path $naudioLicense)) {
    Invoke-WebRequest $naudioLicenseUrl -OutFile $naudioLicense -UseBasicParsing
    $hash = (Get-FileHash $naudioLicense -Algorithm SHA256).Hash
    if ($hash -ne $naudioLicenseSha256) { Remove-Item $naudioLicense; throw "NAudio license hash mismatch: $hash" }
}

$obj = Join-Path $here 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$versionCs = Join-Path $obj 'Version.g.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("AudioSwitch")]
[assembly: AssemblyProduct("AudioSwitch")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
namespace AudioSwitch { static class AppInfo { public const string Version = "$version"; } }
"@ | Set-Content $versionCs -Encoding ASCII

$bin = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$bin\AudioSwitch.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:"$naudio" `
    "$here\AudioSwitch.cs" "$versionCs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item $naudio $bin -Force
"Built $bin\AudioSwitch.exe $version"
