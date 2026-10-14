# Builds WindUpKey Release and/or Testing zips for the Dalamud custom repo.
# Output:
#   deploy/dist/WindUpKey.zip          (Release — public/default version)
#   deploy/dist/WindUpKey-Testing.zip  (Testing compile — same version, adds the self-test / Testing manifest)
#
# Release and Testing are built from the same commit with the same version (11-D2). The Wonderland repo.json catalog
# (github.com/alicewonderland-dev/Wonderland) currently has no Testing link: WindUpKey-Testing.zip is for local or
# private use only (it adds the self-test). Debug helpers are runtime-gated via Configuration.IsDebugEnabled,
# not by the Testing compile channel.
#
# 11: after each build the DLL's metadata names are checked. Release must have Call and daily quests but no
# self-test; Testing must have both (a positive control for the check itself). A mismatch fails the pack.

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

# Metadata names are UTF-8 in the DLL, so a plain search of its bytes finds them.
$selfTestNames = @(
    'WindUpKey.SelfTest',
    'SelfTestController',
    'SelfTestHost',
    'SelfTestTap',
    'TapVerdict',
    'CallIdleFact',
    'RelayIdentityDiagnosis',
    'SnapshotHookResults',
    'SetSelfTestTap',
    'ResidentialTerritoryIds',
    'ISigScanner'
)
$shippedNames = @(
    'CallTravelService',
    'CallPromptWindow',
    'CallTravelDebugLog',
    'PendingCallBook',
    'HousingYesnoMatcher',
    'CallAskFirst',
    'CallHandoffRules',
    'QuestWindService',
    'QuestContentCatalog',
    'QuestDifficulty',
    'IPartyList',
    'SendCallAsync',
    'AddQuestWind'
)

function Assert-DllNames {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DllPath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Release', 'Testing')]
        [string]$Configuration
    )

    # Latin-1 keeps one char per byte, so the ASCII names match where they are.
    $bytes = [System.IO.File]::ReadAllBytes($DllPath)
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)

    $missing = @($shippedNames | Where-Object { -not $text.Contains($_) })
    $unexpected = @()
    if ($Configuration -eq 'Release') {
        $unexpected = @($selfTestNames | Where-Object { $text.Contains($_) })
    } else {
        $missing += @($selfTestNames | Where-Object { -not $text.Contains($_) })
    }

    $problems = @()
    if ($missing.Count -gt 0) { $problems += "missing: $($missing -join ', ')" }
    if ($unexpected.Count -gt 0) { $problems += "must not contain: $($unexpected -join ', ')" }
    if ($problems.Count -gt 0) {
        throw "$Configuration DLL check failed ($DllPath): $($problems -join '; ')"
    }

    Write-Host "$Configuration DLL check passed ($($shippedNames.Count) shipped names present; self-test names $(if ($Configuration -eq 'Release') { 'absent' } else { 'present' }))."
}

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

    Assert-DllNames -DllPath (Join-Path $outDir "WindUpKey.dll") -Configuration $Configuration

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
