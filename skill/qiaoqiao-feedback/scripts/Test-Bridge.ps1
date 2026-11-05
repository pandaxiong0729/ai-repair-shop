[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $TestRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$startScript = Join-Path $PSScriptRoot 'Start-Feedback.ps1'
$readScript = Join-Path $PSScriptRoot 'Read-Feedback.ps1'
$null = New-Item -ItemType Directory -Path $TestRoot -Force
$results = New-Object System.Collections.Generic.List[object]
function Assert-True([bool] $Value, [string] $Message) { if (-not $Value) { throw $Message } }
function New-Session { & $startScript -SessionRoot $TestRoot -NoLaunch | ConvertFrom-Json }
function Set-Batch($Session, [int] $Total = 3, [string] $Tool = 'Hammer', [string] $Reason = '') {
    $batch = [ordered]@{ schema = 1; sessionId = $Session.sessionId; roundId = $Session.sessionId; endedUtc = [DateTimeOffset]::UtcNow.ToString('o'); trigger = 'send'; total = $Total; tools = @(@{ tool = $Tool; count = $Total }); characters = @(@{ character = 'Codex'; count = $Total }); reason = $Reason }
    [System.IO.File]::WriteAllText((Join-Path $Session.sessionDirectory 'batch.json'), ($batch | ConvertTo-Json -Depth 5 -Compress), (New-Object System.Text.UTF8Encoding($false)))
}
function Read-Session($Session) { & $readScript -SessionDirectory $Session.sessionDirectory -SessionId $Session.sessionId -WaitSeconds 0 | ConvertFrom-Json }
function Check([string] $Name, [scriptblock] $Action) {
    try { & $Action; $results.Add([pscustomobject]@{ name = $Name; passed = $true }) }
    catch { $results.Add([pscustomobject]@{ name = $Name; passed = $false; error = $_.Exception.Message }) }
}

Check 'pending produces no corrective prompt or claim' {
    $session = New-Session
    $response = Read-Session $session
    Assert-True ($response.status -eq 'pending') 'Expected pending.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $session.sessionDirectory 'claimed.json'))) 'Pending round was claimed.'
}
Check 'one batch emits one compact prompt and repeated reads do not replay' {
    $session = New-Session
    Set-Batch $session -Total 37 -Reason 'Please fix the missed constraint.'
    $response = Read-Session $session
    Assert-True ($response.status -eq 'feedback') 'No feedback returned.'
    Assert-True ($response.roundId -eq $session.sessionId) 'Wrong round ID.'
    Assert-True ($response.prompt.Contains('37')) 'Aggregated count missing.'
    Assert-True ($response.prompt.Contains('用户很生气')) 'The requested dissatisfaction message is missing.'
    Assert-True ($response.prompt.Length -lt 600) 'Prompt too verbose.'
    Assert-True ((Read-Session $session).status -eq 'already_claimed') 'Round was replayed.'
}
Check 'new whip tool is accepted' {
    $session = New-Session
    Set-Batch $session -Tool 'Whip' -Total 15
    Assert-True ((Read-Session $session).status -eq 'feedback') 'Whip batch rejected.'
}
Check 'empty round returns no prompt' {
    $session = New-Session
    @{ schema = 1; sessionId = $session.sessionId; roundId = $session.sessionId; status = 'empty' } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $session.sessionDirectory 'ended.json') -Encoding UTF8
    Assert-True ((Read-Session $session).status -eq 'empty') 'Expected empty.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $session.sessionDirectory 'claimed.json'))) 'Empty round claimed.'
}
Check 'explicit cancellation ends waiting without a prompt' {
    $session = New-Session
    @{ schema = 1; sessionId = $session.sessionId; roundId = $session.sessionId; status = 'cancelled' } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $session.sessionDirectory 'ended.json') -Encoding UTF8
    Assert-True ((Read-Session $session).status -eq 'cancelled') 'Cancellation did not end waiting.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $session.sessionDirectory 'claimed.json'))) 'Cancelled round claimed.'
}
Check 'reason-only round is accepted without inventing interactions' {
    $session = New-Session
    $batch = @{ schema = 1; sessionId = $session.sessionId; roundId = $session.sessionId; total = 0; tools = @(); reason = 'Please address the original question.' }
    $batch | ConvertTo-Json -Depth 4 -Compress | Set-Content -LiteralPath (Join-Path $session.sessionDirectory 'batch.json') -Encoding UTF8
    Assert-True ((Read-Session $session).status -eq 'feedback') 'Reason-only batch rejected.'
}
Check 'wrong session binding is rejected before claiming' {
    $session = New-Session
    Set-Batch $session
    $rejected = $false
    try { $null = & $readScript -SessionDirectory $session.sessionDirectory -SessionId ([Guid]::NewGuid()) -WaitSeconds 0 } catch { $rejected = $true }
    Assert-True $rejected 'Wrong binding accepted.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $session.sessionDirectory 'claimed.json'))) 'Wrong binding claimed.'
}
Check 'unknown tools and invalid totals are rejected' {
    $session = New-Session
    Set-Batch $session -Tool 'Unknown'
    $rejected = $false
    try { $null = Read-Session $session } catch { $rejected = $true }
    Assert-True $rejected 'Unknown tool accepted.'
    Set-Batch $session
    $batchPath = Join-Path $session.sessionDirectory 'batch.json'
    $batch = Get-Content -LiteralPath $batchPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $batch.total = 4
    $batch | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $batchPath -Encoding UTF8
    $rejected = $false
    try { $null = Read-Session $session } catch { $rejected = $true }
    Assert-True $rejected 'Mismatched total accepted.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $session.sessionDirectory 'claimed.json'))) 'Invalid batch claimed.'
}
Check 'concurrent consumers emit exactly one local result' {
    $session = New-Session
    Set-Batch $session -Total 123
    $jobs = @()
    try {
        for ($index = 0; $index -lt 2; $index++) {
            $jobs += Start-Job -ScriptBlock { param($Script, $Directory, $Id) & $Script -SessionDirectory $Directory -SessionId $Id -WaitSeconds 0 } -ArgumentList $readScript, $session.sessionDirectory, $session.sessionId
        }
        $responses = @($jobs | Receive-Job -Wait | ForEach-Object { $_ | ConvertFrom-Json })
        Assert-True (@($responses | Where-Object status -eq 'feedback').Count -eq 1) 'Expected exactly one feedback result.'
        Assert-True (@($responses | Where-Object status -eq 'already_claimed').Count -eq 1) 'Expected one duplicate suppression.'
    }
    finally { $jobs | Remove-Job -Force }
}
$failed = @($results | Where-Object { -not $_.passed }).Count
[ordered]@{ passed = ($failed -eq 0); failures = $failed; tests = $results.ToArray(); testRoot = (Resolve-Path -LiteralPath $TestRoot).ProviderPath } | ConvertTo-Json -Depth 5
if ($failed -gt 0) { throw "$failed bridge tests failed." }
