# The battle sandbox's host (docs/battle-sandbox.md): small real worlds built from
# compositions you make in the Unity BattleSandbox scene, and the /v2/dev/* routes
# it drives. Compositions are saved as JSON in sandbox/ (gitignored). A dev tool.
#
#   powershell -ExecutionPolicy Bypass -File tools/battle-sandbox.ps1
#
# It builds to its own folder, so a running sandbox never locks the dev build.
param(
    [string]$Folder = "sandbox",
    [int]$Port = 8095
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$out = Join-Path $env:TEMP "aow-battle-sandbox"
Write-Host "Building Sim.Server to $out ..."
& $dotnet build src/Sim.Server/Sim.Server.csproj -c Release -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Starting the battle sandbox host on http://localhost:$Port/ (Ctrl+C to stop)"
& $dotnet (Join-Path $out "Sim.Server.dll") --sandbox $Folder --port $Port
