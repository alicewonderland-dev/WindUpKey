# Builds WindUpKey (Release) and packs the Dalamud install zip.
# Output: deploy/dist/WindUpKey.zip

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot "WindUpKey.sln"))) {
    $repoRoot = $PSScriptRoot
    if (-not (Test-Path (Join-Path $repoRoot "WindUpKey.sln"))) {
        $repoRoot = Split-Path -Parent $PSScriptRoot
    }
}

$project = Join-Path $repoRoot "WindUpKey\WindUpKey.csproj"
$outDir = Join-Path $repoRoot "WindUpKey\bin\Release"
$distDir = Join-Path $repoRoot "deploy\dist"
$stageDir = Join-Path $distDir "stage"
$zipPath = Join-Path $distDir "WindUpKey.zip"

Write-Host "Building Release..."
dotnet build $project -c Release --nologo
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
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

foreach ($name in $required) {
    Copy-Item (Join-Path $outDir $name) (Join-Path $stageDir $name) -Force
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -Force
Remove-Item $stageDir -Recurse -Force

Write-Host "Packed: $zipPath"
Get-Item $zipPath | Format-List FullName, Length, LastWriteTime
