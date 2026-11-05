Set-StrictMode -Version Latest

function Get-ResidentDataRoot([string] $RequestedRoot) {
    if ([string]::IsNullOrWhiteSpace($RequestedRoot)) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable; supply -DataRoot.' }
        $RequestedRoot = Join-Path $env:LOCALAPPDATA 'QiaoQiaoAI\resident'
    }
    return [IO.Path]::GetFullPath($RequestedRoot)
}

function Test-ResidentChildPath([string] $Candidate, [string] $Parent) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@('\', '/'))
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    return $candidateFull.StartsWith($parentFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function ConvertTo-ResidentNativeArgument([string] $Value) {
    # Win32 command-line quoting, including embedded quotes and trailing backslashes.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Get-ResidentArguments([string] $DataRoot, [string] $CodexPath) {
    $values = @('--resident', '--resident-dir', $DataRoot)
    if (-not [string]::IsNullOrWhiteSpace($CodexPath)) { $values += @('--codex-path', $CodexPath) }
    return (($values | ForEach-Object { ConvertTo-ResidentNativeArgument $_ }) -join ' ')
}

function Read-ResidentInstall([string] $DataRoot) {
    $manifestPath = Join-Path $DataRoot 'install.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 16384) { throw 'Resident installation metadata is too large.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.kind -ne 'QiaoQiaoAI.Resident.install' -or
        -not (Test-ResidentChildPath $manifest.executable (Join-Path $DataRoot 'apps')) -or
        [IO.Path]::GetFileName($manifest.executable) -ne 'QiaoQiaoAI.Preview.exe') {
        throw 'Invalid resident installation metadata. No executable was started.'
    }
    return $manifest
}

function Write-ResidentJson([string] $Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12 -Compress), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            # PowerShell may coerce a null backup argument to an empty string on .NET.
            $backup = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.previous'
            try { [IO.File]::Replace($temporary, $Path, $backup) }
            finally { if (Test-Path -LiteralPath $backup -PathType Leaf) { Remove-Item -LiteralPath $backup -Force } }
        }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
}

function Start-ResidentProcess([string] $Executable, [string] $DataRoot, [string] $CodexPath) {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Resident runtime is missing: $Executable" }
    if (-not [string]::IsNullOrWhiteSpace($CodexPath) -and -not (Test-Path -LiteralPath $CodexPath -PathType Leaf)) {
        throw "Codex executable is missing: $CodexPath"
    }
    # This is the requested visible desktop pet; helpers spawned by the executable stay hidden.
    $process = Start-Process -FilePath $Executable -ArgumentList (Get-ResidentArguments $DataRoot $CodexPath) -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Normal -PassThru
    return $process.Id
}

function Get-ResidentStartupPath {
    $startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if ([string]::IsNullOrWhiteSpace($startup)) { throw 'The per-user Startup directory is unavailable.' }
    return Join-Path $startup 'AI Repair Pet.lnk'
}

function Test-OwnedResidentShortcut($Shortcut, [string] $DataRoot, [string] $LocalAppDataRoot = $env:LOCALAPPDATA) {
    if ($Shortcut.Description -ne 'QiaoQiaoAI resident desktop pet (owned shortcut)' -or
        [IO.Path]::GetFileName($Shortcut.TargetPath) -ne 'QiaoQiaoAI.Preview.exe') { return $false }
    if (Test-ResidentChildPath $Shortcut.TargetPath (Join-Path $DataRoot 'apps')) { return $true }

    # MSIX can rewrite our .lnk target to Codex's physical LocalCache path while
    # retaining the logical --resident-dir. Accept only that alias of this install.
    if ([string]::IsNullOrWhiteSpace($LocalAppDataRoot) -or
        -not (Test-ResidentChildPath $DataRoot $LocalAppDataRoot)) { return $false }
    $packages = Join-Path $LocalAppDataRoot 'Packages'
    if (-not (Test-ResidentChildPath $Shortcut.TargetPath $packages)) { return $false }
    $relative = [IO.Path]::GetFullPath($Shortcut.TargetPath).Substring([IO.Path]::GetFullPath($packages).TrimEnd('\').Length + 1)
    if ($relative -notmatch '^OpenAI\.Codex_[^\\]+\\LocalCache\\Local\\(.+)$') { return $false }
    $logicalTarget = [IO.Path]::GetFullPath((Join-Path $LocalAppDataRoot $Matches[1]))
    if (-not (Test-ResidentChildPath $logicalTarget (Join-Path $DataRoot 'apps'))) { return $false }
    $expectedArguments = Get-ResidentArguments $DataRoot ''
    if ($Shortcut.Arguments -ne $expectedArguments -and
        -not $Shortcut.Arguments.StartsWith($expectedArguments + ' ', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if (-not (Test-Path -LiteralPath $logicalTarget -PathType Leaf) -or
        -not (Test-Path -LiteralPath $Shortcut.TargetPath -PathType Leaf)) { return $false }
    return (Get-FileHash -LiteralPath $logicalTarget -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath $Shortcut.TargetPath -Algorithm SHA256).Hash
}

function Disable-ResidentStartup([string] $DataRoot) {
    # On-demand pets never register for Windows login. Migrate only our owned shortcut.
    $startupPath = Get-ResidentStartupPath
    $removed = $false
    if (Test-Path -LiteralPath $startupPath -PathType Leaf) {
        $shell = New-Object -ComObject WScript.Shell
        try {
            $shortcut = $shell.CreateShortcut($startupPath)
            if (Test-OwnedResidentShortcut $shortcut $DataRoot) {
                Remove-Item -LiteralPath $startupPath -Force
                $removed = $true
            }
        }
        finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
    $installed = Read-ResidentInstall $DataRoot
    if ($null -ne $installed -and ($installed.startupEnabled -or $null -ne $installed.startupPath)) {
        $installed.startupEnabled = $false
        $installed.startupPath = $null
        Write-ResidentJson (Join-Path $DataRoot 'install.json') $installed
    }
    return $removed
}
