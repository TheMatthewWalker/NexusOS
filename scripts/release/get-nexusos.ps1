# Get NexusOS (Windows): downloads the newest NexusOS release's installer
# image, checks it and puts it together, ready to write to a USB stick.
#
#   Double-click Get-NexusOS.cmd (Get-NexusOS-Slim.cmd for the smaller image
#   without the local AI model, which downloads once NexusOS is online), or in PowerShell:
#   irm https://raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.ps1 | iex
#
# GitHub limits each release file to 2 GB, so the image is published in
# parts. This fetches every part (going on where it stopped if interrupted),
# checks each against the release's SHA256SUMS, joins them into one .iso in
# Downloads and checks that too. Windows PowerShell 5.1 or newer.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = if ($env:NEXUSOS_REPO) { $env:NEXUSOS_REPO } else { 'TheMatthewWalker/NexusOS' }
$Api = if ($env:NEXUSOS_API) { $env:NEXUSOS_API } else { "https://api.github.com/repos/$Repo/releases" }
$Tag = if ($env:NEXUSOS_TAG) { $env:NEXUSOS_TAG } else { 'latest' }
$Slim = $env:NEXUSOS_EDITION -eq 'slim'
$Dir = if ($env:NEXUSOS_DIR) { $env:NEXUSOS_DIR } else { Join-Path $env:USERPROFILE 'Downloads' }

function Fail($message) { Write-Host "get-nexusos: $message" -ForegroundColor Red; exit 1 }
function Hash($path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
$curl = Get-Command curl.exe -ErrorAction SilentlyContinue
function Fetch($url, $path) {
    # curl.exe (in Windows 10 and 11) goes on where an interrupted download stopped.
    if ($curl) { & $curl.Source -fL --retry 5 --retry-delay 5 -C - -o $path $url; return }
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $path
}

$url = if ($Tag -eq 'latest') { "$Api/latest" } elseif ($Tag.StartsWith('v')) { "$Api/tags/$Tag" } else { "$Api/tags/v$Tag" }
try { $release = Invoke-RestMethod -UseBasicParsing -Uri $url -Headers @{ Accept = 'application/vnd.github+json' } }
catch { Fail "could not find the release ($url)" }
function Asset($name) { ($release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1).browser_download_url }

New-Item -ItemType Directory -Force -Path $Dir | Out-Null
Set-Location -LiteralPath $Dir
$sumsUrl = Asset 'SHA256SUMS'
if (-not $sumsUrl) { Fail "release $($release.tag_name) has no SHA256SUMS" }
Invoke-WebRequest -UseBasicParsing -Uri $sumsUrl -OutFile 'SHA256SUMS'
$sums = @{}
foreach ($line in Get-Content 'SHA256SUMS') { $f = $line -split '\s+', 2; if ($f.Count -eq 2) { $sums[$f[1].TrimStart('*')] = $f[0] } }
# The images (names without .part-): the full one, and from 0.2 a -slim one.
$images = @($sums.Keys | Where-Object { $_ -notlike '*.part-*' } | Sort-Object)
$iso = if ($Slim) { $images | Where-Object { $_ -like '*-slim-*' } | Select-Object -First 1 }
       else { $images | Where-Object { $_ -notlike '*-slim-*' } | Select-Object -First 1 }
if ($Slim -and -not $iso) { Fail "release $($release.tag_name) has no slim image; use Get-NexusOS.cmd" }
$parts = @($sums.Keys | Where-Object { $_.StartsWith("$iso.part-") } | Sort-Object)
if (-not $iso -or $parts.Count -eq 0) { Fail 'SHA256SUMS does not list the image and its parts' }

if ((Test-Path -LiteralPath $iso) -and (Hash $iso) -eq $sums[$iso]) {
    Write-Host "$iso is already here and checks out."
} else {
    Write-Host "NexusOS $($release.tag_name): downloading $($parts.Count) parts into $Dir"
    foreach ($part in $parts) {
        if ((Test-Path -LiteralPath $part) -and (Hash $part) -eq $sums[$part]) { Write-Host "  ${part}: already here"; continue }
        $partUrl = Asset $part
        if (-not $partUrl) { Fail "release $($release.tag_name) has no $part" }
        for ($attempt = 1; ; $attempt++) {
            Write-Host "  $part"
            try { Fetch $partUrl $part } catch { }
            if ((Test-Path -LiteralPath $part) -and (Hash $part) -eq $sums[$part]) { break }
            Write-Host "  $part did not check out; downloading it again"
            Remove-Item -LiteralPath $part -ErrorAction SilentlyContinue
            if ($attempt -ge 3) { Fail "$part did not match SHA256SUMS three times; try again later" }
        }
    }
    Write-Host "Joining the parts into $iso"
    $joining = "$iso.joining"
    $out = [IO.File]::Create((Join-Path $Dir $joining))
    try {
        foreach ($part in $parts) {
            $in = [IO.File]::OpenRead((Join-Path $Dir $part))
            try { $in.CopyTo($out) } finally { $in.Dispose() }
            Remove-Item -LiteralPath $part
        }
    } finally { $out.Dispose() }
    if ((Hash $joining) -ne $sums[$iso]) { Remove-Item -LiteralPath $joining; Fail 'the joined image did not match SHA256SUMS; run me again' }
    Move-Item -Force -LiteralPath $joining -Destination $iso
}

Write-Host ''
Write-Host "Done: $(Join-Path $Dir $iso) (checked against the release's SHA256SUMS)." -ForegroundColor Green
Write-Host 'Write it to a USB stick of 16 GB or more, or 8 GB for the slim image (everything on the stick is erased) with Rufus (https://rufus.ie)'
Write-Host 'or balenaEtcher (https://etcher.balena.io): choose the .iso, choose the stick, Start. In Rufus, pick "DD Image mode" if it asks.'
Write-Host "Then start the computer from the stick. Full steps: https://github.com/$Repo/blob/main/docs/INSTALL.md"
if (-not $env:NEXUSOS_DIR) { Start-Process explorer.exe "/select,`"$(Join-Path $Dir $iso)`"" }
