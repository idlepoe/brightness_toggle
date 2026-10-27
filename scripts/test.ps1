$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
$resultDir = Join-Path $projectRoot "TestResults"
New-Item -ItemType Directory -Force $resultDir | Out-Null
$resultFile = Join-Path $resultDir (([Guid]::NewGuid().ToString()) + ".txt")
$testProcess = Start-Process -FilePath (Join-Path $projectRoot "publish/BrightnessToggle.exe") -ArgumentList @("--self-test", ('"' + $resultFile + '"')) -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(30000)) { throw "Test timeout" }
Get-Content -LiteralPath $resultFile
if ($testProcess.ExitCode -ne 0) { throw "Test failed" }
