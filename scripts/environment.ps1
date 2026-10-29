function Get-SpaceLensDotNet {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $candidates = @(
        'D:\Program Files\dotnet\dotnet.exe'
        'C:\Program Files\dotnet\dotnet.exe'
    )
    if ($env:DOTNET_ROOT) { $candidates = @((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) + $candidates }
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    Push-Location $ProjectRoot
    try {
        foreach ($candidate in ($candidates | Select-Object -Unique)) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            $version = & $candidate --version 2>$null
            if ($LASTEXITCODE -ne 0 -or "$version" -notmatch '^9\.0\.3\d\d$') { continue }
            $runtimes = & $candidate --list-runtimes
            if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft.WindowsDesktop.App 9\.0\.')) { continue }
            # Apply only to this PowerShell process and its children; do not edit machine settings.
            $env:DOTNET_ROOT = Split-Path -Parent $candidate
            $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
            return $candidate
        }
    } finally { Pop-Location }
    throw '需要 global.json 指定的 .NET 9.0.3xx SDK（至少 9.0.319）和 .NET 9 Windows Desktop Runtime x64。可通过 DOTNET_ROOT 指定安装目录。'
}
