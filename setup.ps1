$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\environment.ps1')
$sdkExe = Get-SpaceLensDotNet -ProjectRoot $PSScriptRoot
Push-Location $PSScriptRoot
try {
    Write-Output ('SDK: ' + (& $sdkExe --version))
    Write-Output ('dotnet: ' + $sdkExe)
    & $sdkExe restore SpaceLens.sln
    if ($LASTEXITCODE -ne 0) { throw '依赖还原失败。' }
    & $sdkExe build SpaceLens.sln -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw '开发环境构建失败。' }
    Write-Output '开发环境就绪。运行 .\run.ps1；检查并打包运行 .\build.ps1 -Test。'
} finally { Pop-Location }
