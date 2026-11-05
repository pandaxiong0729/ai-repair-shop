[CmdletBinding()]
param([string] $DataRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resident-Common.ps1')
$DataRoot = Get-ResidentDataRoot $DataRoot
$removedStartup = Disable-ResidentStartup $DataRoot
$manifest = Read-ResidentInstall $DataRoot
$stopRequested = $false
$stopError = $null
$remaining = @()
if ($null -ne $manifest) {
    $remaining = @(Get-Process QiaoQiaoAI.Preview -ErrorAction SilentlyContinue | Where-Object Path -eq $manifest.executable)
    if ($remaining.Count -gt 0) {
        try {
            $stopArguments = (@('--resident-stop', '--resident-dir', $DataRoot) | ForEach-Object { ConvertTo-ResidentNativeArgument $_ }) -join ' '
            $request = Start-Process -FilePath $manifest.executable -ArgumentList $stopArguments -WorkingDirectory (Split-Path -Parent $manifest.executable) -WindowStyle Hidden -PassThru
            if (-not $request.WaitForExit(5000)) { throw 'Stop request timed out; no process was force-killed.' }
            if ($request.ExitCode -ne 0) { throw ('Stop helper returned exit code ' + $request.ExitCode) }
            $stopRequested = $true
            foreach ($residentProcess in $remaining) { $null = $residentProcess.WaitForExit(5000) }
        }
        catch { $stopError = $_.Exception.Message }
        $remaining = @(Get-Process QiaoQiaoAI.Preview -ErrorAction SilentlyContinue | Where-Object Path -eq $manifest.executable)
    }
}
[ordered]@{
    status = $(if ($remaining.Count -gt 0) { 'stop_pending' } elseif ($stopRequested) { 'stopped' } else { 'not_running' })
    dataRoot = $DataRoot; filesPreserved = $true; stopRequested = $stopRequested
    closePetToExit = ($remaining.Count -gt 0); stopError = $stopError; removedStartup = $removedStartup; startupEnabled = $false
} | ConvertTo-Json -Compress
