[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$archive = Join-Path $outputRoot 'QiaoQiaoAI-source.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = @('Directory.Build.props', '.gitignore', 'README.md', 'README.zh-CN.md', 'CONTRIBUTING.md', 'LICENSE', 'NOTICE') |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) }
foreach ($folder in @('src', 'tests', 'scripts', 'packaging', 'docs', 'skill')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse |
        Where-Object { $_.FullName.Substring($root.Length + 1) -notmatch '(^|[\\/])(bin|obj|media|desktop)([\\/]|$)' }
}
# The original text configuration is public. Keep image assets out of the source export.
foreach ($name in @('skin.json', 'doubao-skin.json', 'deepseek-skin.json', 'quotes.json')) {
    $config = Join-Path $root ('runtime-assets/codex/' + $name)
    if (Test-Path -LiteralPath $config -PathType Leaf) { $files += Get-Item -LiteralPath $config }
}
$stream = [System.IO.File]::Open($archive, [System.IO.FileMode]::Create)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in ($files | Sort-Object FullName -Unique)) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ('QiaoQiaoAI/' + $relative), [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose(); $stream.Dispose() }
Write-Output $archive
