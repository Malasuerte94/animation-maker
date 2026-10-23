param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$buildProps.Project.PropertyGroup.Version
$appSource = Join-Path $repoRoot 'artifacts\portable\AnimationMaker'
$releaseOutput = Join-Path $repoRoot "artifacts\releases\$version"
if (-not (Test-Path -LiteralPath (Join-Path $appSource 'AnimationMaker.exe'))) { & (Join-Path $PSScriptRoot 'publish.ps1') }
if (-not $CompilerPath) {
    $candidates = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'))
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { $candidates += $found.Source }
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup, or pass -CompilerPath with the path to ISCC.exe.' }
New-Item -ItemType Directory -Force -Path $releaseOutput | Out-Null
& $CompilerPath "/DAppVersion=$version" "/DAppSource=$appSource" "/DReleaseOutput=$releaseOutput" (Join-Path $PSScriptRoot 'installer\AnimationMaker.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
& (Join-Path $PSScriptRoot 'update-release-checksums.ps1') -ReleaseDirectory $releaseOutput
Write-Host "Installer: $releaseOutput\AnimationMaker-$version-win-x64-setup.exe"
