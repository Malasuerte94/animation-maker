param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$packages = @{}
foreach ($projectName in @('AnimationMaker.Core', 'AnimationMaker.Gpu', 'AnimationMaker.App')) {
    $assetsPath = Join-Path $repoRoot "src\$projectName\obj\project.assets.json"
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $packages.ContainsKey($library.Name)) { continue }
        $packageRoot = $null
        foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $cache $library.Value.path
            if (Test-Path -LiteralPath $candidate) { $packageRoot = $candidate; break }
        }
        if (-not $packageRoot) { throw "Package source directory missing: $($library.Name)" }
        $packageName, $packageVersion = $library.Name -split '/', 2
        $target = Join-Path $licenses "$packageName-$packageVersion"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($notice in Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_]?PARTY)' }) {
            $relative = $notice.FullName.Substring($packageRoot.TrimEnd('\').Length + 1)
            $noticeTarget = Join-Path $target $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $noticeTarget) | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $noticeTarget
        }
        $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' | Select-Object -First 1
        if ($nuspec) { Copy-Item -LiteralPath $nuspec.FullName -Destination $target }
        $packages[$library.Name] = [ordered]@{ name = $packageName; version = $packageVersion; nuget = "https://www.nuget.org/packages/$packageName/$packageVersion" }
    }
}
$dotnetRoot = Join-Path $repoRoot '.tools\dotnet'
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    if (Test-Path -LiteralPath (Join-Path $dotnetRoot $name)) {
        New-Item -ItemType Directory -Force -Path (Join-Path $licenses 'dotnet') | Out-Null
        Copy-Item -LiteralPath (Join-Path $dotnetRoot $name) -Destination (Join-Path $licenses 'dotnet')
    }
}
$vorticeLicense = Join-Path $licenses 'Vortice-MIT-LICENSE.txt'
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/amerkoleci/Vortice.Windows/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE' -OutFile $vorticeLicense
$packages.Values | Sort-Object name,version | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $licenses 'packages.json') -Encoding UTF8
