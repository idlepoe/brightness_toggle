param([string]$Dotnet = "dotnet")
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
& $Dotnet publish (Join-Path $projectRoot "src/BrightnessToggle.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $projectRoot "publish")
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
