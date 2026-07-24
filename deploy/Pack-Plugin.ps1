# Builds WindUpKey Release and/or Testing zips for the Dalamud custom repo.
# Output:
#   deploy/dist/WindUpKey.zip          (Release — public/default version)
#   deploy/dist/WindUpKey-Testing.zip  (Testing compile — higher AssemblyVersion / Testing manifest)
#
# Public testing = upload WindUpKey-Testing.zip and set repo.json TestingAssemblyVersion +
# DownloadLinkTesting (opt-in). Private testing = use the zip/DLL locally only; do not link it
# from public repo.json. Debug helpers are runtime-gated via Configuration.IsDebugEnabled,
# not by the Testing compile channel.

param(
    [ValidateSet('Release', 'Testing', 'Both')]
    [string]$Channel = 'Release'
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot "WindUpKey.sln"))) {
    $repoRoot = $PSScriptRoot
    if (-not (Test-Path (Join-Path $repoRoot "WindUpKey.sln"))) {
        $repoRoot = Split-Path -Parent $PSScriptRoot
    }
}

$project = Join-Path $repoRoot "WindUpKey\WindUpKey.csproj"
$distDir = Join-Path $repoRoot "deploy\dist"

function Clear-GeneratedReadOnly {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    & attrib.exe -R $Path *> $null
    if (Test-Path -LiteralPath $Path -PathType Container) {
        & attrib.exe -R (Join-Path $Path '*') /S /D *> $null
    }
}

# Generated output can retain Windows' ReadOnly attribute after a build or copy.
# Clear it before MSBuild and DalamudPackager update or replace those files.
Clear-GeneratedReadOnly (Join-Path $repoRoot "WindUpKey\bin")
Clear-GeneratedReadOnly (Join-Path $repoRoot "WindUpKey\obj")
Clear-GeneratedReadOnly $distDir
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

function Pack-Channel {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Release', 'Testing')]
        [string]$Configuration
    )

    $outDir = Join-Path $repoRoot "WindUpKey\bin\$Configuration"
    $stageDir = Join-Path $distDir ("stage-" + $Configuration.ToLowerInvariant())
    $zipName = if ($Configuration -eq 'Testing') { 'WindUpKey-Testing.zip' } else { 'WindUpKey.zip' }
    $zipPath = Join-Path $distDir $zipName

    Write-Host "Building $Configuration..."
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $required = @(
        "WindUpKey.dll",
        "WindUpKey.json",
        "WindUpKey.Protocol.dll"
    )

    foreach ($name in $required) {
        $path = Join-Path $outDir $name
        if (-not (Test-Path $path)) {
            throw "Missing build output: $path"
        }
    }

    $iconPath = Join-Path $outDir "images\icon.png"
    if (-not (Test-Path $iconPath)) {
        throw "Missing build output: $iconPath"
    }

    if (Test-Path $stageDir) {
        Clear-GeneratedReadOnly $stageDir
        Remove-Item $stageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    foreach ($name in $required) {
        Copy-Item (Join-Path $outDir $name) (Join-Path $stageDir $name) -Force
    }

    $stageImages = Join-Path $stageDir "images"
    New-Item -ItemType Directory -Path $stageImages -Force | Out-Null
    Copy-Item $iconPath (Join-Path $stageImages "icon.png") -Force

    # Bundled WAVs load from Sounds\ next to the DLL (see Plugin.cs / SoundEffectService).
    $soundsSrc = Join-Path $outDir "Sounds"
    if (-not (Test-Path $soundsSrc)) {
        throw "Missing build output: $soundsSrc (expected windingup.wav / windingdown.wav)"
    }
    $wavs = @(Get-ChildItem -Path $soundsSrc -Filter "*.wav" -File -ErrorAction SilentlyContinue)
    if ($wavs.Count -eq 0) {
        throw "Missing build output: no .wav files under $soundsSrc"
    }
    $stageSounds = Join-Path $stageDir "Sounds"
    New-Item -ItemType Directory -Path $stageSounds -Force | Out-Null
    Copy-Item (Join-Path $soundsSrc "*.wav") $stageSounds -Force

    if (Test-Path $zipPath) {
        Clear-GeneratedReadOnly $zipPath
        Remove-Item $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -Force
    Remove-Item $stageDir -Recurse -Force

    Write-Host "Packed: $zipPath"
    Get-Item $zipPath | Format-List FullName, Length, LastWriteTime
}

$channels = if ($Channel -eq 'Both') { @('Release', 'Testing') } else { @($Channel) }
foreach ($c in $channels) {
    Pack-Channel -Configuration $c
}
