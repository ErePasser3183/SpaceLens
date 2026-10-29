param([string]$Version = '1.1.4', [string]$OutputDirectory = 'dist\Releases')
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
    # Keep Velopack's install/update layout, but collect the destination before invoking it.
    $payloadDirectory = Join-Path $PSScriptRoot ('artifacts\installer-payload\' + $Version)
    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
    $payload = Join-Path $payloadDirectory 'Setup.exe'
    $setup = Join-Path $OutputDirectory 'SpaceLens-win-Setup.exe'
    Copy-Item -LiteralPath $setup -Destination $payload -Force
    $installerOutput = Join-Path $PSScriptRoot ('artifacts\installer\' + $Version)
    & $sdkExe build src\SpaceLens.Installer -c Release "-p:Version=$Version" "-p:SetupPayload=$payload" -o $installerOutput
    if ($LASTEXITCODE -ne 0) { throw '安装界面构建失败。' }
    Copy-Item -LiteralPath (Join-Path $installerOutput 'SpaceLens-win-Setup.exe') -Destination $setup -Force
    $checkDirectory = Join-Path $PSScriptRoot ('artifacts\installer-checks\' + $Version)
    $check = Start-Process -FilePath (Resolve-Path $setup).Path -ArgumentList @('--verify-installer', ('"' + $checkDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "安装界面检查失败：$checkDirectory" }
} finally { Pop-Location }
