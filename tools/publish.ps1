param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet) -or -not (Test-Path -LiteralPath (Join-Path $repoRoot '.tools\ffmpeg\UPSTREAM-README.txt'))) { & (Join-Path $PSScriptRoot 'bootstrap.ps1') }
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') }
& (Join-Path $PSScriptRoot 'build-delta-runtime.ps1')
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$buildProps.Project.PropertyGroup.Version
$portableRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\portable'))
$output = Join-Path $portableRoot 'AnimationMaker'
$staging = Join-Path $portableRoot ('.publish-' + [Guid]::NewGuid().ToString('N'))
$releaseOutput = Join-Path $repoRoot "artifacts\releases\$version"
New-Item -ItemType Directory -Force -Path $releaseOutput | Out-Null
function Remove-PublishStaging([string]$OwnedPath) {
    $resolved = [IO.Path]::GetFullPath($OwnedPath)
    if (-not $resolved.StartsWith($portableRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clean a path outside the portable build directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
try {
    & $dotnet publish (Join-Path $repoRoot 'src\AnimationMaker.App\AnimationMaker.App.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $staging --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    foreach ($required in @('AnimationMaker.exe', 'AnimationMaker.pri', 'App.xbf', 'MainWindow.xbf', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'ffmpeg\ffmpeg.exe', 'ffmpeg\ffprobe.exe', 'delta-runtime\delta-runtime.js', 'delta-runtime\delta-runtime.iife.js', 'delta-runtime\delta-runtime.d.ts', 'delta-runtime\package.json', 'delta-runtime\README.md', 'delta-runtime\LICENSE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Portable dependency missing: $required" }
    }
    foreach ($doc in @('README.md', 'VALIDATION.md', 'DELTA-FORMAT.md', 'THIRD-PARTY-NOTICES.md', 'RELEASE-NOTES.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repoRoot $doc) -Destination $staging }
    Copy-Item -LiteralPath (Join-Path $repoRoot '.tools\ffmpeg\UPSTREAM-README.txt') -Destination (Join-Path $staging 'ffmpeg')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'third-party\ffmpeg\SOURCE.md') -Destination (Join-Path $staging 'ffmpeg\SOURCE.md')
    $ffmpeg = Join-Path $staging 'ffmpeg\ffmpeg.exe'
    $buildInfo = @('Unmodified FFmpeg 9.0.2 essentials build from Gyan Doshi.', 'Original archive SHA-256: 60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba', 'FFmpeg source revision: 946fcce07b', '')
    $buildInfo += (& $ffmpeg -version 2>&1 | ForEach-Object { [string]$_ })
    foreach ($binary in @('ffmpeg.exe', 'ffprobe.exe')) { $buildInfo += ((Get-FileHash -LiteralPath (Join-Path $staging "ffmpeg\$binary") -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $binary) }
    $buildInfo | Set-Content -LiteralPath (Join-Path $staging 'ffmpeg\BUILD-CONFIGURATION.txt') -Encoding UTF8
    & (Join-Path $PSScriptRoot 'collect-licenses.ps1') -OutputDirectory $staging
    if (Get-Process -Name AnimationMaker -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) }) { throw 'The portable app is running from the output directory. Close that copy before publishing.' }
    Remove-PublishStaging $output
    if (-not ([IO.Path]::GetFullPath($staging)).StartsWith($portableRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid publish staging path.' }
    Move-Item -LiteralPath $staging -Destination $output
    $zip = Join-Path $releaseOutput "AnimationMaker-$version-win-x64-portable.zip"
    Compress-Archive -LiteralPath $output -DestinationPath $zip -Force
    $runtimePackageRoot = Join-Path $portableRoot ('.runtime-' + [Guid]::NewGuid().ToString('N'))
    try {
        $runtimePackage = Join-Path $runtimePackageRoot 'animation-maker-pixi-delta'
        New-Item -ItemType Directory -Force -Path $runtimePackage | Out-Null
        foreach ($file in @('delta-runtime.js', 'delta-runtime.iife.js', 'delta-runtime.d.ts', 'package.json', 'README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $output "delta-runtime\$file") -Destination $runtimePackage }
        Compress-Archive -LiteralPath $runtimePackage -DestinationPath (Join-Path $releaseOutput "AnimationMaker-PixiJS-Delta-$version.zip") -Force
    } finally { Remove-PublishStaging $runtimePackageRoot }
    $sourceArchive = Join-Path $releaseOutput 'FFmpeg-9.0.2-source.tar.gz'
    if (-not (Test-Path -LiteralPath $sourceArchive)) { Invoke-WebRequest -Uri 'https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/946fcce07b' -OutFile $sourceArchive }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'RELEASE-NOTES.md') -Destination $releaseOutput
    & (Join-Path $PSScriptRoot 'update-release-checksums.ps1') -ReleaseDirectory $releaseOutput
    Write-Host "Portable app: $output\AnimationMaker.exe"
    Write-Host "Release assets: $releaseOutput"
} finally { Remove-PublishStaging $staging }
