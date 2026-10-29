param([string]$ScanPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\environment.ps1')
$sdkExe = Get-SpaceLensDotNet -ProjectRoot $PSScriptRoot
if ($ScanPath) { $ScanPath = [IO.Path]::GetFullPath($ScanPath) }
Push-Location $PSScriptRoot
try {
    $arguments = @('run', '--project', 'src\SpaceLens.App', '-c', 'Debug', '--')
    if ($ScanPath) { $arguments += $ScanPath }
    & $sdkExe @arguments
    if ($LASTEXITCODE -ne 0) { throw '程序退出时发生错误。' }
} finally { Pop-Location }
