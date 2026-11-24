# Checks for a newer Audio Over IP release and test-builds headless.patch against it.
# Exit codes: 0 up to date, 10 new release builds cleanly, 20 new release needs the patch updated.
# Native tools are judged by exit code; Windows PowerShell would turn their stderr into errors under 'Stop'.
$ErrorActionPreference = 'Continue'
$pin = Get-Content (Join-Path $PSScriptRoot 'UPSTREAM.json') -Raw -ErrorAction Stop | ConvertFrom-Json
# Upstream marks releases as pre-releases, which /releases/latest skips, so take the newest non-draft from the list.
try {
    $releases = Invoke-RestMethod "https://api.github.com/repos/$($pin.repo)/releases?per_page=20" -Headers @{ 'User-Agent' = 'audioswitch-upstream-check' } -ErrorAction Stop
}
catch {
    "Couldn't list upstream releases: $($_.Exception.Message)"
    exit 1
}
$latest = ($releases | Where-Object { -not $_.draft } | Sort-Object { [datetime]$_.published_at } -Descending | Select-Object -First 1).tag_name
if (-not $latest) {
    'No upstream releases found.'
    exit 1
}

if ($latest -eq $pin.tag) {
    "Up to date: $latest"
    exit 0
}

"New upstream release: $latest (pinned $($pin.tag))"
$out = Join-Path ([IO.Path]::GetTempPath()) ("aoip-check-" + [guid]::NewGuid())
try {
    & (Join-Path $PSScriptRoot 'build-sender.ps1') -Tag $latest -OutDir $out
    "headless.patch applies and builds against $latest. Test it, then set tag and commit in sender/UPSTREAM.json."
    exit 10
}
catch {
    "headless.patch needs updating for ${latest}: $($_.Exception.Message)"
    "Conflicts should only be in main.go's build tag line and wcatools/com.go."
    exit 20
}
finally { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue }
