[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $CodexPath,
    [string] $DataRoot,
    [switch] $NoStartup, # Compatibility only: all starts are now on demand.
    [switch] $StatusOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resident-Common.ps1')
$DataRoot = Get-ResidentDataRoot $DataRoot
if ($StatusOnly) {
    $statusPath = Join-Path $DataRoot 'status.json'
    if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
        if ((Get-Item -LiteralPath $statusPath).Length -gt 65536) { throw 'Resident status file is too large.' }
        $status = Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
        [ordered]@{ status = 'snapshot'; dataRoot = $DataRoot; lastWrittenUtc = (Get-Item -LiteralPath $statusPath).LastWriteTimeUtc.ToString('o'); resident = $status } | ConvertTo-Json -Depth 12 -Compress
    }
    else { [ordered]@{ status = 'not_started'; dataRoot = $DataRoot } | ConvertTo-Json -Compress }
    return
}
$null = Disable-ResidentStartup $DataRoot
$manifest = Read-ResidentInstall $DataRoot
$bundledExecutable = Join-Path $PSScriptRoot '..\assets\desktop\QiaoQiaoAI.Preview.exe'
$bundledDll = Join-Path $PSScriptRoot '..\assets\desktop\QiaoQiaoAI.Preview.dll'
if ($null -ne $manifest -and [string]::IsNullOrWhiteSpace($ExePath) -and (Test-Path -LiteralPath $bundledDll -PathType Leaf)) {
    $bundledVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($bundledDll).FileVersion)
    if ($bundledVersion -gt [Version]$manifest.version) {
        # An updated skill must not silently keep launching the previously installed runtime.
        $ownedProcesses = @(Get-Process QiaoQiaoAI.Preview -ErrorAction SilentlyContinue | Where-Object Path -eq $manifest.executable)
        if ($ownedProcesses.Count -gt 0) {
            $stopArguments = (@('--resident-stop', '--resident-dir', $DataRoot) | ForEach-Object { ConvertTo-ResidentNativeArgument $_ }) -join ' '
            $stopHelper = Start-Process -FilePath $manifest.executable -ArgumentList $stopArguments -WindowStyle Hidden -PassThru
            if (-not $stopHelper.WaitForExit(5000)) { throw 'The previous resident did not acknowledge its stop request. Close it before updating.' }
            foreach ($ownedProcess in $ownedProcesses) {
                if (-not $ownedProcess.WaitForExit(5000)) { throw 'The previous resident is still closing. Close it before updating; no process was force-killed.' }
            }
        }
        & (Join-Path $PSScriptRoot 'Install-Resident.ps1') -ExePath $bundledExecutable -CodexPath $CodexPath -DataRoot $DataRoot
        return
    }
}
if ($null -eq $manifest -or -not (Test-Path -LiteralPath $manifest.executable -PathType Leaf) -or -not [string]::IsNullOrWhiteSpace($ExePath)) {
    & (Join-Path $PSScriptRoot 'Install-Resident.ps1') -ExePath $ExePath -CodexPath $CodexPath -DataRoot $DataRoot
    return
}
if ([string]::IsNullOrWhiteSpace($CodexPath)) { $CodexPath = $manifest.codexPath }
$processId = Start-ResidentProcess $manifest.executable $DataRoot $CodexPath
[ordered]@{ status = 'launch_requested'; executable = $manifest.executable; dataRoot = $DataRoot; statusFile = (Join-Path $DataRoot 'status.json'); processId = $processId; startupEnabled = $manifest.startupEnabled } | ConvertTo-Json -Compress
