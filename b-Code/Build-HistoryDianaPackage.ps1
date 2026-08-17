[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$HistoryVulcanPackageRoot = $env:HISTORYVULCAN_PACKAGE_ROOT
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'z-Publish'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = $publishRoot
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not ($OutputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
          $OutputRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw "OutputRoot must remain inside the HistoryDiana project or the process temp directory: $OutputRoot"
}

$projectPath = Join-Path $repoRoot 'b-Code-HistoryDiana\HistoryDiana.csproj'
$manifestSource = Join-Path $repoRoot 'b-Code-HistoryDiana\module.manifest.json'
$packageDocuments = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Office-Diana\package') -Filter '*.md' -File)
if ($packageDocuments.Count -eq 0) {
    throw 'b-Office-Diana/package must contain at least one Markdown document'
}
$releaseRoot = Join-Path $repoRoot "b-Code-HistoryDiana\bin\$Configuration\net8.0-windows"
$historyVulcanRoot = if ([string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    [IO.Path]::GetFullPath((Join-Path $repoRoot '..\2026-023-HistoryVulcan\z-Publish'))
}
else {
    [IO.Path]::GetFullPath($HistoryVulcanPackageRoot)
}
$transactionId = [Guid]::NewGuid().ToString('N')
$transactionRoot = Join-Path ([IO.Path]::GetTempPath()) "HistoryDiana.Package.$transactionId"
$stage = Join-Path $transactionRoot 'candidate'
$backup = Join-Path $transactionRoot 'previous'

function Assert-ModulePackage {
    param([string]$Root, [string]$ExpectedVersion)

    $expectedFiles = @('HistoryDiana.dll', 'HistoryDiana.xml', 'module.manifest.json', 'SHA256SUMS') +
        @($packageDocuments | ForEach-Object { "docs/$($_.Name)" }) | Sort-Object
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $historyPrefix = $rootPrefix + 'history\'
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object { -not $_.FullName.StartsWith($historyPrefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
        $_.FullName.Substring($rootPrefix.Length).Replace('\', '/')
    } | Sort-Object)
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
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>.+)$') {
            throw "Invalid HistoryDiana checksum line: $line"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    $hashTargets = @($expectedFiles | Where-Object { $_ -ne 'SHA256SUMS' })
    foreach ($file in $hashTargets) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $Root $file.Replace('/', '\')) -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($file) -or $hashes[$file] -ne $actual) {
            throw "HistoryDiana checksum mismatch: $file"
        }
    }
    if ($hashes.Count -ne $hashTargets.Count) {
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

New-Item -ItemType Directory -Force -Path $transactionRoot, $backup, $OutputRoot | Out-Null
& dotnet build $projectPath -c $Configuration -p:NuGetAudit=false "-p:HistoryVulcanPackageRoot=$historyVulcanRoot"
if ($LASTEXITCODE -ne 0) {
    throw "HistoryDiana $Configuration build failed with exit code $LASTEXITCODE"
}

try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryDiana.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryDiana.xml') -Destination $stage
    Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $stage 'module.manifest.json')
    $docsRoot = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Force -Path $docsRoot | Out-Null
    foreach ($document in $packageDocuments) {
        Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $docsRoot $document.Name)
    }
    $relativeFiles = @('HistoryDiana.dll', 'HistoryDiana.xml', 'module.manifest.json') +
        @($packageDocuments | ForEach-Object { "docs/$($_.Name)" })
    $checksumLines = foreach ($file in $relativeFiles) {
        "$((Get-FileHash -LiteralPath (Join-Path $stage $file.Replace('/', '\')) -Algorithm SHA256).Hash)  $file"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Assert-ModulePackage $stage $version

    $movedPrevious = [Collections.Generic.List[string]]::new()
    $movedCandidate = [Collections.Generic.List[string]]::new()
    try {
        foreach ($item in @(Get-ChildItem -LiteralPath $OutputRoot -Force |
                Where-Object { $_.Name -ne 'history' })) {
            Move-Item -LiteralPath $item.FullName -Destination $backup
            $movedPrevious.Add($item.Name)
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $stage -Force)) {
            Move-Item -LiteralPath $item.FullName -Destination $OutputRoot
            $movedCandidate.Add($item.Name)
        }
        Assert-ModulePackage $OutputRoot $version
    }
    catch {
        foreach ($name in $movedCandidate) {
            $path = Join-Path $OutputRoot $name
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        foreach ($name in $movedPrevious) {
            $path = Join-Path $backup $name
            if (Test-Path -LiteralPath $path) {
                Move-Item -LiteralPath $path -Destination $OutputRoot
            }
        }
        throw
    }
    Write-Host "HistoryDiana $version candidate package created: $OutputRoot"
}
finally {
    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
