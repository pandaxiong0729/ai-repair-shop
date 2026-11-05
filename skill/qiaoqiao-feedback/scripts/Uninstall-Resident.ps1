[CmdletBinding()]
param([string] $DataRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resident-Common.ps1')
$DataRoot = Get-ResidentDataRoot $DataRoot
$manifest = Read-ResidentInstall $DataRoot
$startupPath = Get-ResidentStartupPath
$removedStartup = $false
if (Test-Path -LiteralPath $startupPath -PathType Leaf) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($startupPath)
        if (Test-OwnedResidentShortcut $shortcut $DataRoot) {
            Remove-Item -LiteralPath $startupPath -Force
            $removedStartup = $true
        }
    }
    finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
if ($null -ne $manifest) {
    $manifest.startupEnabled = $false
    $manifest.startupPath = $null
    Write-ResidentJson (Join-Path $DataRoot 'install.json') $manifest
}
$stopRequested = $false
$stopError = $null
if ($null -ne $manifest -and (Test-Path -LiteralPath $manifest.executable -PathType Leaf)) {
    try {
        $stopArguments = (@('--resident-stop', '--resident-dir', $DataRoot) | ForEach-Object { ConvertTo-ResidentNativeArgument $_ }) -join ' '
        $request = Start-Process -FilePath $manifest.executable -ArgumentList $stopArguments -WorkingDirectory (Split-Path -Parent $manifest.executable) -WindowStyle Hidden -PassThru
        if ($request.WaitForExit(5000)) {
            if ($request.ExitCode -eq 0) { $stopRequested = $true }
            else { $stopError = 'Stop helper returned exit code ' + $request.ExitCode }
        }
        else { $stopError = 'Stop request did not finish within 5 seconds; no process was force-killed.' }
    }
    catch { $stopError = $_.Exception.Message }
}
[ordered]@{ status = $(if ($stopRequested) { 'startup_removed_stop_requested' } else { 'startup_removed' }); removedStartup = $removedStartup; dataRoot = $DataRoot; filesPreserved = $true; stopRequested = $stopRequested; closePetToExit = (-not $stopRequested); stopError = $stopError } | ConvertTo-Json -Compress
