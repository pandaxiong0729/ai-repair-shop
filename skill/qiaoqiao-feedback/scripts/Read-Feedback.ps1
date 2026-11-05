[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $SessionDirectory,
    [Parameter(Mandatory = $true)][Guid] $SessionId,
    [ValidateRange(0, 55)][int] $WaitSeconds = 55
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = (Resolve-Path -LiteralPath $SessionDirectory).ProviderPath
$id = $SessionId.ToString('D')
$manifestPath = Join-Path $directory 'session.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 8192) { throw 'Session manifest too large.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.sessionId -ne $id -or $manifest.target -ne 'current-agent') {
    throw 'Session binding mismatch. Use only the session ID created in this chat.'
}
$claimPath = Join-Path $directory 'claimed.json'
$batchPath = Join-Path $directory 'batch.json'
$endedPath = Join-Path $directory 'ended.json'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    if (Test-Path -LiteralPath $claimPath -PathType Leaf) {
        [ordered]@{ status = 'already_claimed'; roundId = $id; note = 'Do not replay this round. Receipt does not prove model action.' } | ConvertTo-Json -Compress
        return
    }
    if (Test-Path -LiteralPath $batchPath -PathType Leaf) { break }
    if (Test-Path -LiteralPath $endedPath -PathType Leaf) {
        if ((Get-Item -LiteralPath $endedPath).Length -gt 8192) { throw 'End marker too large.' }
        $ended = Get-Content -LiteralPath $endedPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($ended.schema -ne 1 -or $ended.sessionId -ne $id -or $ended.roundId -ne $id -or $ended.status -notin @('empty', 'cancelled')) { throw 'Invalid end marker.' }
        [ordered]@{ status = $ended.status; roundId = $id } | ConvertTo-Json -Compress
        return
    }
    if ([DateTimeOffset]::Parse($manifest.expiresUtc) -le [DateTimeOffset]::UtcNow) {
        [ordered]@{ status = 'expired'; roundId = $id } | ConvertTo-Json -Compress
        return
    }
    if ($clock.Elapsed.TotalSeconds -ge $WaitSeconds) {
        [ordered]@{ status = 'pending'; roundId = $id } | ConvertTo-Json -Compress
        return
    }
    Start-Sleep -Milliseconds 200
}
if ((Get-Item -LiteralPath $batchPath).Length -gt 32768) { throw 'Feedback batch too large.' }
$batch = Get-Content -LiteralPath $batchPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($batch.schema -ne 1 -or $batch.sessionId -ne $id -or $batch.roundId -ne $id) { throw 'Feedback round mismatch.' }
if ($batch.total -isnot [int] -and $batch.total -isnot [long]) { throw 'Invalid total count.' }
if ($batch.total -lt 0 -or $batch.total -gt [int]::MaxValue) { throw 'Invalid total count.' }
$labels = [ordered]@{ Hammer = '橡皮锤'; Poop = '便便'; PaperReject = '纸团退稿'; Stamp = '大印章'; Pinch = '捏脸'; SpringGlove = '弹簧拳套'; Jelly = '果冻'; Egg = '鸡蛋'; Whip = '软鞭' }
$seen = @{}
[long] $sum = 0
$parts = @()
foreach ($entry in @($batch.tools)) {
    $key = [string] $entry.tool
    if (-not $labels.Contains($key) -or $seen.ContainsKey($key)) { throw 'Invalid or duplicate tool type.' }
    if (($entry.count -isnot [int] -and $entry.count -isnot [long]) -or $entry.count -lt 1 -or $entry.count -gt [int]::MaxValue) { throw 'Invalid tool count.' }
    $seen[$key] = $true
    $sum += [long] $entry.count
    $parts += ($labels[$key] + '×' + $entry.count)
}
if ($sum -ne [long] $batch.total) { throw 'Tool counts do not match total.' }
if ($batch.reason -isnot [string] -or $batch.reason.Length -gt 120) { throw 'Invalid feedback reason.' }
$reason = [regex]::Replace($batch.reason, '\s+', ' ').Trim()
if ($batch.total -eq 0 -and [string]::IsNullOrWhiteSpace($reason)) { throw 'An empty round must use ended.json.' }
$detail = if ($parts.Count -gt 0) { $parts -join '、' } else { '仅填写原因' }
$reasonText = if ([string]::IsNullOrWhiteSpace($reason)) { '' } else { ' 用户补充原因：' + ($reason | ConvertTo-Json -Compress) + '。' }
$prompt = '用户很生气，对你本轮回答不满意。反馈ID：' + $id + '；本轮互动共' + $batch.total + '次（' + $detail + '）。' + $reasonText + '请简短道歉，结合当前对话定位问题并给出修正；具体问题不清楚时只问一个关键问题。不要逐次回应、夸大点击含义或声称模型受到惩罚；本ID只处理一次。'

# Claim before returning: concurrent or repeated consumers cannot emit another prompt.
# A crash after this point can lose delivery. Never automatically delete/replay the claim.
$claim = [ordered]@{ schema = 1; sessionId = $id; roundId = $id; claimedUtc = [DateTimeOffset]::UtcNow.ToString('o'); state = 'claimed-not-acknowledged' }
try {
    $stream = [System.IO.File]::Open($claimPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
}
catch [System.IO.IOException] {
    if (Test-Path -LiteralPath $claimPath -PathType Leaf) {
        [ordered]@{ status = 'already_claimed'; roundId = $id; note = 'Do not replay this round.' } | ConvertTo-Json -Compress
        return
    }
    throw
}
try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($claim | ConvertTo-Json -Compress))
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}
finally { $stream.Dispose() }
[ordered]@{ status = 'feedback'; roundId = $id; prompt = $prompt } | ConvertTo-Json -Compress
