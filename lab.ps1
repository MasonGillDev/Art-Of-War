# The balance lab, one command:  .\lab.ps1            (the 160-day curve)
#                                .\lab.ps1 -All       (every AI test)
#                                .\lab.ps1 -Seed 42   (different continent)
# Uses the user-local net10 SDK (the PATH default is net7 and can't build
# this repo — see docs/architecture.md tooling notes).
param(
    [switch]$All,
    [int]$Seed = 0,
    [int]$Size = 0   # map size override (e.g. -Size 128 to mirror the live server)
)

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
$filter = if ($All) { "FullyQualifiedName~AiPlayer" } else { "FullyQualifiedName~BalanceLab" }

if ($Seed -ne 0) {
    # The lab's map seed is hard-coded in MakeMatch (deterministic CI);
    # for ad-hoc seed sweeps set this env var the test harness reads.
    $env:LAB_MAPSEED = $Seed
}
if ($Size -ne 0) { $env:LAB_MAPSIZE = $Size }
try {
    & $dotnet test "$PSScriptRoot\tests\Sim.Tests" `
        /p:BaseOutputPath="$env:TEMP\artofwar-lab\" `
        --nologo --filter $filter --logger "console;verbosity=detailed" 2>$null `
        | Select-String -Pattern "d\s*\d+: pop|Passed!|Failed!|  Failed |Error Message" -Context 0,1
}
finally {
    if ($Seed -ne 0) { Remove-Item Env:\LAB_MAPSEED -ErrorAction SilentlyContinue }
    if ($Size -ne 0) { Remove-Item Env:\LAB_MAPSIZE -ErrorAction SilentlyContinue }
}
