$ErrorActionPreference = 'Stop'
$runtimeRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime\pixi-delta'
Push-Location $runtimeRoot
try {
    & npm.cmd ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Delta runtime npm install failed.' }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'Delta runtime build failed.' }
} finally { Pop-Location }
