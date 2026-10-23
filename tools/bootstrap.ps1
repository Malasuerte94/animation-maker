param([switch]$SkipSdk)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
if (-not $SkipSdk -and -not (Test-Path (Join-Path $toolsRoot 'dotnet\dotnet.exe'))) {
    $installer = Join-Path $toolsRoot 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version '10.0.402' -InstallDir (Join-Path $toolsRoot 'dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw '.NET SDK installation failed.' }
}
$ffmpegRoot = Join-Path $toolsRoot 'ffmpeg'
if (-not (Test-Path (Join-Path $ffmpegRoot 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $ffmpegRoot 'UPSTREAM-README.txt'))) {
    $url = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip'
    $archive = Join-Path $toolsRoot 'ffmpeg.zip'
    $publishedHash = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $publishedHash) { Invoke-WebRequest $url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $publishedHash) { throw 'FFmpeg download failed its SHA-256 check.' }
    $expanded = Join-Path $toolsRoot ('ffmpeg-extract-' + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    $build = Get-ChildItem -LiteralPath $expanded -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force -Path $ffmpegRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $build.FullName 'bin\ffmpeg.exe'),(Join-Path $build.FullName 'bin\ffprobe.exe') -Destination $ffmpegRoot
    Copy-Item -LiteralPath (Join-Path $build.FullName 'LICENSE') -Destination (Join-Path $ffmpegRoot 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $build.FullName 'README.txt') -Destination (Join-Path $ffmpegRoot 'UPSTREAM-README.txt')
}
$dotnet = Join-Path $toolsRoot 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet restore (Join-Path $repoRoot 'AnimationMaker.slnx') -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
Write-Host 'Dependencies ready.'
