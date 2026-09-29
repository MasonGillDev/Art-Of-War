# The battle test bed's host (docs/m41-status.md): small scenario worlds from
# scenarios/battle/*.scenario, a Grid combat world, and the /v2/dev/* routes the
# Unity BattleTestbed scene drives. A dev tool.
#
#   powershell -ExecutionPolicy Bypass -File tools/battle-testbed.ps1
#   powershell -ExecutionPolicy Bypass -File tools/battle-testbed.ps1 -Scenario scenarios/battle/03-two-fronts.scenario
#
# It builds to its own folder, so a running test bed never locks the dev build.
param(
    [string]$Scenario = "scenarios/battle",
    [int]$Port = 8095
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$out = Join-Path $env:TEMP "aow-battle-testbed"
Write-Host "Building Sim.Server to $out ..."
& $dotnet build src/Sim.Server/Sim.Server.csproj -c Release -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Starting the battle test bed host on http://localhost:$Port/ (Ctrl+C to stop)"
& $dotnet (Join-Path $out "Sim.Server.dll") --scenario $Scenario --port $Port
