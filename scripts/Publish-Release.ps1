param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$witnessBuildDir = Join-Path $repositoryRoot "bin\Release\net8.0-windows"
$launcherPublishDir = Join-Path $repositoryRoot "TheWitness.Launcher\bin\Release\net8.0-windows\win-x64-singlefile"
$artifactsRoot = Join-Path $repositoryRoot "artifacts\release"
$packageName = "TheWitness-$Version-win-x64"
$publishDirectory = Join-Path $artifactsRoot $packageName
$archivePath = Join-Path $artifactsRoot "$packageName.zip"
$latestJsonPath = Join-Path $artifactsRoot "latest.json"

dotnet build (Join-Path $repositoryRoot "TheWitness.csproj") -c Release --no-incremental -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: TheWitness" }

dotnet publish (Join-Path $repositoryRoot "TheWitness.Launcher\TheWitness.Launcher.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $launcherPublishDir `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Deterministic=true `
    -p:ContinuousIntegrationBuild=true `
    -p:IncludeSourceRevisionInInformationalVersion=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: TheWitness.Launcher" }

if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$witnessFolder = Join-Path $publishDirectory "Witness"
New-Item -ItemType Directory -Path $witnessFolder -Force | Out-Null

$filesToCopy = @("TheWitness.exe", "TheWitness.dll", "TheWitness.deps.json", "TheWitness.runtimeconfig.json", "System.Management.dll")
foreach ($file in $filesToCopy) {
    Copy-Item (Join-Path $witnessBuildDir $file) -Destination $witnessFolder -Force
}

$runtimesDir = Join-Path $witnessBuildDir "runtimes"
if (Test-Path $runtimesDir) {
    Copy-Item $runtimesDir -Destination $witnessFolder -Recurse -Force
}

Copy-Item (Join-Path $launcherPublishDir "TheWitnessLauncher.exe") -Destination $publishDirectory -Force

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal -Force

$finalHash = (Get-FileHash (Join-Path $witnessFolder "TheWitness.dll") -Algorithm SHA256).Hash

$latestJson = [ordered]@{
    version = $Version
    sha256  = $finalHash
} | ConvertTo-Json
$latestJsonNormalized = ($latestJson -replace "`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($latestJsonPath, $latestJsonNormalized, (New-Object System.Text.UTF8Encoding($false)))

Write-Output ""
Write-Output "Package:     $archivePath"
Write-Output "latest.json: $latestJsonPath"
Write-Output "TheWitness.dll SHA-256: $finalHash"
