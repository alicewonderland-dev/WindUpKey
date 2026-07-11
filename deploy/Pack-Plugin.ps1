# Builds WindUpKey Release and/or Testing zips for the Dalamud custom repo.
# Output:
#   deploy/dist/WindUpKey.zip          (Release — no debug helpers)
#   deploy/dist/WindUpKey-Testing.zip  (Testing — unwind + self-wind)

param(
    [ValidateSet('Release', 'Testing', 'Both')]
    [string]$Channel = 'Both'
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

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    foreach ($name in $required) {
        Copy-Item (Join-Path $outDir $name) (Join-Path $stageDir $name) -Force
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -Force
    Remove-Item $stageDir -Recurse -Force

    Write-Host "Packed: $zipPath"
    Get-Item $zipPath | Format-List FullName, Length, LastWriteTime
}

$channels = if ($Channel -eq 'Both') { @('Release', 'Testing') } else { @($Channel) }
foreach ($c in $channels) {
    Pack-Channel -Configuration $c
}
