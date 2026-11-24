# Builds AudioOverIP-headless.exe: clones the pinned upstream tag, applies headless.patch, builds with -tags headless.
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'bin'),
    [string]$Tag,
    [switch]$SkipCommitCheck
)
# Native tools are judged by exit code; Windows PowerShell would turn their stderr into errors under 'Stop'.
$ErrorActionPreference = 'Continue'
$pin = Get-Content (Join-Path $PSScriptRoot 'UPSTREAM.json') -Raw -ErrorAction Stop | ConvertFrom-Json
if (-not $Tag) { $Tag = $pin.tag }

$go = (Get-Command go -ErrorAction SilentlyContinue).Source
if (-not $go) {
    $go = @(
        (Join-Path $env:USERPROFILE 'Tools\go\bin\go.exe'),
        (Join-Path $env:USERPROFILE 'AppData\Local\Programs\go-toolchain\go\bin\go.exe'),
        'C:\Program Files\Go\bin\go.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $go) { throw 'Go 1.26 or later is required (go.dev/dl).' }
}

$src = Join-Path ([IO.Path]::GetTempPath()) ("aoip-src-" + [guid]::NewGuid())
git clone --quiet --depth 1 --branch $Tag -c advice.detachedHead=false "https://github.com/$($pin.repo).git" $src
if ($LASTEXITCODE -ne 0) { throw "git clone of $Tag failed" }
try {
    $head = (git -C $src rev-parse HEAD).Trim()
    if (-not $SkipCommitCheck -and $Tag -eq $pin.tag -and $head -ne $pin.commit) {
        throw "Tag $Tag points at $head, expected $($pin.commit). Upstream moved the tag."
    }
    git -C $src apply --3way (Join-Path $PSScriptRoot 'headless.patch')
    if ($LASTEXITCODE -ne 0) { throw "headless.patch doesn't apply to $Tag" }

    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $env:CGO_ENABLED = '0'
    Push-Location $src
    try { & $go build -tags headless -trimpath -ldflags '-s -w' -o (Join-Path $OutDir 'AudioOverIP-headless.exe') . }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "go build failed for $Tag" }
    Copy-Item (Join-Path $src 'LICENSE') (Join-Path $OutDir 'LICENSE-EUPL-1.2.txt') -Force
    "Built $OutDir\AudioOverIP-headless.exe from $Tag ($head)"
}
finally { Remove-Item $src -Recurse -Force -ErrorAction SilentlyContinue }
