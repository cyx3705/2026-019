[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'b-Publish'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $publishRoot 'current\HistoryDiana'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside the HistoryDiana project: $OutputRoot"
}

$projectPath = Join-Path $repoRoot 'b-Code-HistoryDiana\HistoryDiana.csproj'
$manifestSource = Join-Path $repoRoot 'b-Code-HistoryDiana\module.manifest.json'
$releaseRoot = Join-Path $repoRoot "b-Code-HistoryDiana\bin\$Configuration\net8.0-windows"
$workRoot = Join-Path $publishRoot 'work'
$transactionId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $workRoot "HistoryDiana-candidate-$transactionId"
$backup = Join-Path $workRoot "HistoryDiana-previous-$transactionId"
$failed = Join-Path $workRoot "HistoryDiana-failed-$transactionId"

function Assert-ModulePackage {
    param([string]$Root, [string]$ExpectedVersion)

    $expectedFiles = @('HistoryDiana.dll', 'HistoryDiana.xml', 'module.manifest.json', 'SHA256SUMS') | Sort-Object
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -File | Select-Object -ExpandProperty Name | Sort-Object)
    if (($actualFiles -join "`n") -ne ($expectedFiles -join "`n")) {
        throw "HistoryDiana package file set is invalid: $($actualFiles -join ', ')"
    }
    $manifest = [IO.File]::ReadAllText((Join-Path $Root 'module.manifest.json'), [Text.UTF8Encoding]::new($false)) |
        ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.type -ne 'HistoryVulcan.Module' -or
        $manifest.name -ne 'HistoryDiana' -or $manifest.version -ne $ExpectedVersion -or
        $manifest.artifact -ne 'HistoryDiana.dll' -or $manifest.docs -ne 'HistoryDiana.xml') {
        throw "HistoryDiana manifest identity does not match $ExpectedVersion"
    }
    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Root 'SHA256SUMS'), [Text.UTF8Encoding]::new($false))) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>[^\\/]+)$') {
            throw "Invalid HistoryDiana checksum line: $line"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    foreach ($file in @('HistoryDiana.dll', 'HistoryDiana.xml', 'module.manifest.json')) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $Root $file) -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($file) -or $hashes[$file] -ne $actual) {
            throw "HistoryDiana checksum mismatch: $file"
        }
    }
    if ($hashes.Count -ne 3) {
        throw 'HistoryDiana checksum does not cover the complete package'
    }
}

# Diana 的版本源是 props 而不是 csproj：与 module-publish.manifest.json 的
# versionProps/versionProperty 指向同一处，三处身份（版本源、源清单、快照清单）才对得上。
$versionPropsPath = Join-Path $repoRoot 'b-Code-HistoryDiana\HistoryDianaVersion.props'
if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
    throw "HistoryDiana version source is missing: $versionPropsPath"
}
$propsXml = [xml][IO.File]::ReadAllText($versionPropsPath, [Text.UTF8Encoding]::new($false))
$versionValues = @($propsXml.Project.PropertyGroup | ForEach-Object {
    if ($_.PSObject.Properties.Name -contains 'HistoryDianaVersion') {
        $_.PSObject.Properties['HistoryDianaVersion'].Value
    }
} | Where-Object { $_ })
if ($versionValues.Count -ne 1) {
    throw 'HistoryDianaVersion.props must declare exactly one HistoryDianaVersion value'
}
$version = [string]$versionValues[0]
$sourceManifest = [IO.File]::ReadAllText($manifestSource, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($version -notmatch '^\d+\.\d+\.\d+$' -or
    $sourceManifest.name -ne 'HistoryDiana' -or $sourceManifest.version -ne $version) {
    throw 'HistoryDiana project version and source manifest are not aligned'
}

New-Item -ItemType Directory -Force -Path $workRoot, (Split-Path -Parent $OutputRoot) | Out-Null
& dotnet build $projectPath -c $Configuration -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
    throw "HistoryDiana $Configuration build failed with exit code $LASTEXITCODE"
}

$promoted = $false
$backedUp = $false
try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryDiana.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryDiana.xml') -Destination $stage
    Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $stage 'module.manifest.json')
    $checksumLines = foreach ($file in @('HistoryDiana.dll', 'HistoryDiana.xml', 'module.manifest.json')) {
        "$((Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash)  $file"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Assert-ModulePackage $stage $version

    try {
        if (Test-Path -LiteralPath $OutputRoot) {
            Move-Item -LiteralPath $OutputRoot -Destination $backup
            $backedUp = $true
        }
        Move-Item -LiteralPath $stage -Destination $OutputRoot
        $promoted = $true
        Assert-ModulePackage $OutputRoot $version
        if ($backedUp) {
            Remove-Item -LiteralPath $backup -Recurse -Force
            $backedUp = $false
        }
    }
    catch {
        if ($promoted -and (Test-Path -LiteralPath $OutputRoot)) {
            Move-Item -LiteralPath $OutputRoot -Destination $failed
        }
        if ($backedUp -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $OutputRoot
        }
        throw
    }
    Write-Host "HistoryDiana $version candidate package created: $OutputRoot"
}
finally {
    foreach ($path in @($stage, $backup, $failed)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
