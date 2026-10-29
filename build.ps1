param([switch]$Test, [switch]$Standalone)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\environment.ps1')
$sdkExe = Get-SpaceLensDotNet -ProjectRoot $PSScriptRoot
Push-Location $PSScriptRoot
try {
    if ($Test) {
        & $sdkExe run --project tests\SpaceLens.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw '核心检查失败。' }
    }
    # Separate output directories keep standalone runtime files out of the small package.
    $package = if ($Standalone) { 'SpaceLens-Standalone' } else { 'SpaceLens' }
    $output = Join-Path $PSScriptRoot ('dist\' + $package)
    $selfContained = if ($Standalone) { 'true' } else { 'false' }
    & $sdkExe publish src\SpaceLens.App -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
    if ($LASTEXITCODE -ne 0) { throw '生成失败。' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\使用说明.md') -Destination (Join-Path $output '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\第三方参考.md') -Destination (Join-Path $output '第三方参考.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\licenses') -Destination $output -Recurse -Force
    Write-Output (Join-Path $output 'SpaceLens.exe')
} finally { Pop-Location }
