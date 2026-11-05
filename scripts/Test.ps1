[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [Parameter(Mandatory = $true)][string]$VPetSourceRoot,
    [Parameter(Mandatory = $true)][string]$AssetsDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$VPetSourceRoot = (Resolve-Path -LiteralPath $VPetSourceRoot).Path
$AssetsDir = (Resolve-Path -LiteralPath $AssetsDir).Path
$baseline = '5a64ad2bf00a3f321227a26aa585bac6399b1a0e'
$commit = & git -C $VPetSourceRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -ne $baseline) {
    throw "These integration checks target VPet commit $baseline. Check out that baseline first."
}
$reportDir = Join-Path $root 'artifacts/validation'
$testRoot = Join-Path $root ('artifacts/integration/run-' + [Guid]::NewGuid().ToString('N'))
$bin = Join-Path $testRoot 'bin'
$testAssets = Join-Path $testRoot 'assets'
$null = New-Item -ItemType Directory -Path $reportDir, $testAssets -Force
foreach ($file in @('skin.json', 'spritesheet.png', 'doubao.png', 'doubao-skin.json', 'doubao-spritesheet.png', 'deepseek-reference.png', 'deepseek-skin.json', 'deepseek-spritesheet.png', 'quotes.json', 'dialogue.json', 'effects.json')) {
    $source = Join-Path $AssetsDir $file
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $testAssets $file) -Force
    }
}
$preview = Join-Path $root 'artifacts/preview/QiaoQiaoAI.Preview.exe'
if (-not (Test-Path -LiteralPath $preview)) { throw 'Run scripts/Build.ps1 before Test.ps1.' }
$uiReport = Join-Path $reportDir 'ui-tests.json'
$process = Start-Process -FilePath $preview -ArgumentList @('--assets', ('"' + $AssetsDir + '"'), '--self-test', ('"' + $uiReport + '"')) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "UI checks failed. See $uiReport" }
& $DotnetPath build (Join-Path $root 'tests/QiaoQiaoAI.Integration/QiaoQiaoAI.Integration.csproj') -c Release "-p:VPetSourceRoot=$VPetSourceRoot" -o $bin
if ($LASTEXITCODE -ne 0) { throw 'Integration harness build failed.' }
$integrationReport = Join-Path $reportDir 'integration-tests.json'
& $DotnetPath (Join-Path $bin 'QiaoQiaoAI.Integration.dll') --report $integrationReport
if ($LASTEXITCODE -ne 0) { throw "Integration checks failed. See $integrationReport" }
Write-Output "Checks passed. Reports: $reportDir"
