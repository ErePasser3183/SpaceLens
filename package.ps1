param([string]$Version = '1.1.0', [string]$OutputDirectory = 'dist\Releases')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw '版本号格式无效。' }
. (Join-Path $PSScriptRoot 'scripts\environment.ps1')
$sdkExe = Get-SpaceLensDotNet -ProjectRoot $PSScriptRoot
Push-Location $PSScriptRoot
try {
    & $sdkExe tool restore
    if ($LASTEXITCODE -ne 0) { throw '打包工具还原失败。' }
    $publish = Join-Path $PSScriptRoot ('artifacts\package\' + $Version)
    & $sdkExe publish src\SpaceLens.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $publish
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
    Copy-Item docs\使用说明.md,docs\第三方参考.md -Destination $publish
    Copy-Item -LiteralPath docs\licenses -Destination $publish -Recurse -Force
    & $sdkExe tool run vpk -- pack --packId SpaceLens --packVersion $Version --packDir $publish --mainExe SpaceLens.exe --packTitle SpaceLens --packAuthors ErePasser3183 --channel win --runtime win-x64 --icon src\SpaceLens.App\Assets\SpaceLens.ico --outputDir $OutputDirectory --skip-updates
    if ($LASTEXITCODE -ne 0) { throw '更新包生成失败。' }
} finally { Pop-Location }
