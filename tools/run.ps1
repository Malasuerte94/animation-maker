param([string]$Open)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { & (Join-Path $PSScriptRoot 'bootstrap.ps1') }
if (-not (Test-Path (Join-Path $repoRoot 'runtime\pixi-delta\dist\delta-runtime.iife.js'))) { & (Join-Path $PSScriptRoot 'build-delta-runtime.ps1') }
$arguments = @('run','--project',(Join-Path $repoRoot 'src\AnimationMaker.App\AnimationMaker.App.csproj'),'-p:Platform=x64','--')
if ($Open) { $arguments += @('--open', $Open) }
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'The application failed to start.' }
