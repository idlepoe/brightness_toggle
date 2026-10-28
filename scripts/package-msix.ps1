param([string]$Dotnet = "dotnet", [string]$MakeAppx = "")
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $MakeAppx) {
    $sdkTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $sdkTool) { throw "Install the Windows SDK (MakeAppx.exe) first." }
    $MakeAppx = $sdkTool.FullName
}
& (Join-Path $PSScriptRoot "build.ps1") -Dotnet $Dotnet
$stage = Join-Path $projectRoot "build/msix-stage"
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $projectRoot "publish/BrightnessToggle.exe") $stage -Force
Copy-Item (Join-Path $projectRoot "packaging/AppxManifest.xml") $stage -Force
Copy-Item (Join-Path $projectRoot "packaging/Assets") $stage -Recurse -Force
$destination = Join-Path $projectRoot "publish/BrightnessToggle-1.0.2-x64-Store.msix"
& $MakeAppx pack /d $stage /p $destination /o
if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed" }
Write-Output "Unsigned Store submission package: $destination"
