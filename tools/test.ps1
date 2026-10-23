$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { & (Join-Path $PSScriptRoot 'bootstrap.ps1') }
& $dotnet test (Join-Path $repoRoot 'tests\AnimationMaker.Tests\AnimationMaker.Tests.csproj') --nologo --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
