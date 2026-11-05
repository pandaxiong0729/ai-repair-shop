[CmdletBinding()]
param(
    [Alias('ExeOverride')][string] $ExePath,
    [string] $SessionRoot = (Join-Path $env:LOCALAPPDATA 'QiaoQiaoAI\feedback'),
    [switch] $NoLaunch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot '..\assets\desktop\QiaoQiaoAI.Preview.exe'
}
if (-not $NoLaunch) {
    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        throw 'The skill desktop runtime is missing. Install the complete QiaoQiaoAI-feedback-skill-local package, or provide -ExePath.'
    }
    $ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
}
$null = New-Item -ItemType Directory -Path $SessionRoot -Force
$sessionId = [Guid]::NewGuid().ToString('D')
$directory = Join-Path ((Resolve-Path -LiteralPath $SessionRoot).ProviderPath) $sessionId
$null = New-Item -ItemType Directory -Path $directory
$manifest = [ordered]@{
    schema = 1
    sessionId = $sessionId
    target = 'current-agent'
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    expiresUtc = [DateTimeOffset]::UtcNow.AddHours(4).ToString('o')
}
[System.IO.File]::WriteAllText((Join-Path $directory 'session.json'), ($manifest | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
$processId = $null
if (-not $NoLaunch) {
    # The user asked to interact with this desktop pet; its window should be visible.
    $launched = Start-Process -FilePath $ExePath -ArgumentList @('--feedback-dir', ('"{0}"' -f $directory)) -WorkingDirectory (Split-Path -Parent $ExePath) -WindowStyle Normal -PassThru
    $processId = $launched.Id
}
[ordered]@{ status = $(if ($NoLaunch) { 'prepared' } else { 'listening' }); sessionId = $sessionId; sessionDirectory = $directory; processId = $processId } | ConvertTo-Json -Compress
