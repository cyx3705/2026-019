[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Module,
    [switch]$Publish,
    [switch]$AllowDirtySource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dianaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectsRoot = [IO.Path]::GetFullPath((Join-Path $dianaRoot '..'))
$transactionId = [Guid]::NewGuid().ToString('N')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$workRoot = Join-Path $dianaRoot "b-Publish\work\module-release-$transactionId"

# 普通 module 的定义与验证步骤由注册表提供；新增普通模块只需新增一项 JSON。
$registryPath = Join-Path $dianaRoot 'b-Code\module-publish.manifest.json'
if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
    throw "Module publish registry is missing: $registryPath"
}
$registry = [IO.File]::ReadAllText($registryPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($registry.schemaVersion -ne 1) {
    throw "Unsupported module publish registry schema: $($registry.schemaVersion)"
}
$definitions = @{}
foreach ($entry in @($registry.modules)) {
    if ([string]::IsNullOrWhiteSpace([string]$entry.name) -or $entry.kind -ne 'module') {
        throw 'Every registry module must declare a non-empty name and kind=module.'
    }
    if ($definitions.ContainsKey([string]$entry.name)) {
        throw "Duplicate module publish registry entry: $($entry.name)"
    }
    $definitions[[string]$entry.name] = $entry
}

# Vulcan 是宿主，保留宿主快照与门禁的特例配置。
$definitions['HistoryVulcan'] = [ordered]@{
        Kind = 'host'
        ProjectDirectory = '2026-023-HistoryVulcan'
        VersionProps = 'b-Code-HistoryVulcan\VulcanVersion.props'
        VersionProperty = 'VulcanVersion'
        # 宿主没有"源 manifest"：身份由版本源加快照 manifest 表达，没有第三处可漂移。
        SourceManifest = ''
        SnapshotManifest = 'manifest.json'
        IdentityProperty = 'product'
        CandidateDirectory = 'b-Publish\current'
        FormalDirectory = 'z-HistoryVulcan'
        BuildScript = 'b-Code-HistoryVulcan\eng\Build-HistoryVulcanPackage.ps1'
        PackageDocuments = 'b-Office\package'
        ContractScript = ''
        SmokeProject = ''
        UiSmokeProject = ''
        TestProject = 'b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj'
        GateScripts = @(
            'b-Code-HistoryVulcan\eng\Test-QualityGate.ps1',
            'b-Code-HistoryVulcan\eng\Assert-PublicApiBaseline.ps1'
        )
}

function Update-CommandManual {
    <#
        .SYNOPSIS
        发布后刷新全体系命令面总览。

        .DESCRIPTION
        命令面是跨模块的横切事实：任何一个模块部署都会改变它，因此刷新挂在每次发布之后，
        而不是由某个模块自己负责——由模块负责就意味着别人发布时它会过期。

        文档落在 Diana 的 b-Office-OneHistory：它既不是某个项目的 package 文档（那是各项目
        自己的复用说明），也不是元文件（那是「定义」），而是从运行时注册表导出的跨模块事实。

        用宿主的 --export-command-manual 无头入口而不是 vulcan.command.manual 命令：
        后者带本地二次确认闸口，无人值守跑不了，而为它开「跳过确认」的口子会削弱确认语义。

        本步骤失败不回滚已完成的发布：手册是发布的产物而非前提，缺一份手册不该让一次
        已经通过全部门禁的发布作废。失败会明确告警，可用同一命令手动补齐。
    #>
    $hostExecutable = Join-Path $projectsRoot '2026-023-HistoryVulcan\z-HistoryVulcan\host\HistoryVulcan.exe'
    if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
        Write-Warning "命令面总览未刷新：找不到已发布宿主 $hostExecutable"
        return
    }

    $manualPath = Join-Path $dianaRoot 'b-Office-OneHistory\命令面总览.md'
    try {
        & $hostExecutable '--export-command-manual' $manualPath
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "命令面总览刷新失败（退出码 $LASTEXITCODE）：$manualPath"
            return
        }
        Write-Host "Command manual: $manualPath"
    }
    catch {
        Write-Warning "命令面总览刷新失败：$($_.Exception.Message)"
    }
}

function Repair-VulcanAutostart {
    param([Parameter(Mandatory = $true)][string]$ModuleRoot)

    $hostExecutable = Join-Path $ModuleRoot 'host\HistoryVulcan.exe'
    if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
        Write-Warning "HistoryVulcan 登录启动未修复：正式快照中找不到 $hostExecutable"
        return
    }

    try {
        & $hostExecutable '--repair-autostart'
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "HistoryVulcan 登录启动修复失败（退出码 $LASTEXITCODE）"
            return
        }
        Write-Host "HistoryVulcan 登录启动已按正式 Z 快照修复"
    }
    catch {
        Write-Warning "HistoryVulcan 登录启动修复失败：$($_.Exception.Message)"
    }
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its allowed root: $fullPath"
    }
    return $fullPath
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host "[$Module] $Description"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Read-ModuleVersion {
    param([string]$PropsPath, [string]$PropertyName)

    [xml]$props = [IO.File]::ReadAllText($PropsPath, [Text.UTF8Encoding]::new($false))
    $values = @($props.Project.PropertyGroup | ForEach-Object {
        if ($_.PSObject.Properties.Name -contains $PropertyName) {
            $_.PSObject.Properties[$PropertyName].Value
        }
    } | Where-Object { $_ })
    if ($values.Count -ne 1 -or [string]$values[0] -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version source must declare exactly one semantic version property ${PropertyName}: $PropsPath"
    }
    return [string]$values[0]
}

function Assert-ModuleSnapshot {
    param(
        [string]$Root,
        [string]$ExpectedName,
        [string]$ExpectedVersion,
        [string]$ManifestName = 'module.manifest.json',
        [string]$IdentityProperty = 'name',
        [string]$Kind = 'module'
    )

    $manifestPath = Join-Path $Root $ManifestName
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "Module snapshot is incomplete: $Root"
    }

    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($manifest.$IdentityProperty -ne $ExpectedName -or $manifest.version -ne $ExpectedVersion) {
        throw "Module snapshot identity mismatch: expected $ExpectedName $ExpectedVersion"
    }

    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines($sumsPath, [Text.UTF8Encoding]::new($false))) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64}) [ *](?<file>.+)$') {
            throw "Invalid SHA256SUMS line in ${Root}: $line"
        }
        if ($hashes.ContainsKey($Matches.file)) {
            throw "Duplicate checksum entry in ${Root}: $($Matches.file)"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }

    # Janus and host snapshots have nested directories; flat modules produce the same relative keys as file names.
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object Name -ne 'SHA256SUMS')
    if ($files.Count -ne $hashes.Count) {
        throw "SHA256SUMS does not cover the complete snapshot ($($files.Count) files vs $($hashes.Count) entries): $Root"
    }
    foreach ($file in $files) {
        $key = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($key) -or $hashes[$key] -ne $actual) {
            throw "Checksum mismatch in ${Root}: $key"
        }
    }
}

function New-ConsumerDocumentMirror {
    param(
        [string]$SourceRoot,
        [string]$Destination,
        [string]$ModuleName,
        [string]$Version,
        [string]$ProjectDirectory,
        [string]$SourceCommit,
        [bool]$SourceDirty
    )

    $documents = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.md' -File | Sort-Object Name)
    if ($documents.Count -eq 0) {
        throw "No consumer Markdown documents found: $SourceRoot"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $entries = foreach ($document in $documents) {
        $target = Join-Path $Destination $document.Name
        Copy-Item -LiteralPath $document.FullName -Destination $target
        [ordered]@{
            file = $document.Name
            bytes = (Get-Item -LiteralPath $target).Length
            sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    }

    $mirrorManifest = [ordered]@{
        schemaVersion = 1
        module = $ModuleName
        version = $Version
        sourceProject = $ProjectDirectory
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        sourceDocumentRoot = 'b-Office/package'
        artifactSnapshot = "../../../../$ProjectDirectory/z-$ModuleName"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        documents = @($entries)
    }
    [IO.File]::WriteAllText(
        (Join-Path $Destination 'manifest.json'),
        ($mirrorManifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Assert-ConsumerDocumentMirror {
    param([string]$Root, [string]$ExpectedModule, [string]$ExpectedVersion)

    $manifestPath = Join-Path $Root 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Consumer document mirror manifest is missing: $Root"
    }
    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($manifest.module -ne $ExpectedModule -or $manifest.version -ne $ExpectedVersion) {
        throw "Consumer document mirror identity mismatch: $Root"
    }
    foreach ($document in @($manifest.documents)) {
        $path = Join-Path $Root ([string]$document.file)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Mirrored consumer document is missing: $path"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne [string]$document.sha256) {
            throw "Mirrored consumer document checksum mismatch: $path"
        }
    }
}

function Invoke-ConfiguredModuleValidation {
    param(
        [Parameter(Mandatory = $true)][object[]]$Steps,
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    foreach ($step in $Steps) {
        if ([string]::IsNullOrWhiteSpace([string]$step.tool) -or
            @($step.arguments).Count -eq 0) {
            throw 'Every module validation step must declare a tool and arguments.'
        }
        $configurations = if ($step.PSObject.Properties.Name -contains 'configurations') {
            @($step.configurations | ForEach-Object { [string]$_ })
        } else {
            @('')
        }
        foreach ($configuration in $configurations) {
            $moduleOutput = ''
            if ($step.PSObject.Properties.Name -contains 'moduleOutputRoot') {
                $moduleOutput = Join-Path $ProjectRoot "$($step.moduleOutputRoot)\$configuration\net8.0-windows"
            }
            $capturePath = Join-Path $workRoot 'ui-smoke-320x680-dark.png'
            $isolatedOutputBase = if ($step.PSObject.Properties.Name -contains 'isolatedOutputRoot') {
                Join-Path $ProjectRoot ([string]$step.isolatedOutputRoot)
            } else {
                Join-Path $workRoot 'test-output'
            }
            $isolatedOutputRoot = $isolatedOutputBase.TrimEnd([IO.Path]::DirectorySeparatorChar) +
                [IO.Path]::DirectorySeparatorChar
            $arguments = @($step.arguments | ForEach-Object {
                ([string]$_).Replace('{configuration}', $configuration).
                    Replace('{moduleOutput}', $moduleOutput).
                    Replace('{capturePath}', $capturePath).
                    Replace('{isolatedOutputRoot}', $isolatedOutputRoot)
            })
            $description = ([string]$step.description).Replace('{configuration}', $configuration)
            try {
                Invoke-Checked ([string]$step.tool) $arguments $ProjectRoot $description
            }
            finally {
                if ($step.PSObject.Properties.Name -contains 'isolatedOutputRoot' -and
                    (Test-Path -LiteralPath $isolatedOutputBase)) {
                    Remove-Item -LiteralPath $isolatedOutputBase -Recurse -Force
                }
                $legacyOutputRoot = "$isolatedOutputBase$configuration"
                if ($step.PSObject.Properties.Name -contains 'isolatedOutputRoot' -and
                    $legacyOutputRoot -ne $isolatedOutputBase -and
                    (Test-Path -LiteralPath $legacyOutputRoot)) {
                    Remove-Item -LiteralPath $legacyOutputRoot -Recurse -Force
                }
            }
        }
    }
}

$definition = $definitions[$Module]
if ($null -eq $definition) {
    throw "Module '$Module' is not registered. Add a kind=module entry to $registryPath."
}
$projectRoot = Assert-ChildPath (Join-Path $projectsRoot $definition.ProjectDirectory) $projectsRoot 'Module project'
$versionPropsPath = Join-Path $projectRoot $definition.VersionProps
$sourceManifestPath = Join-Path $projectRoot $definition.SourceManifest
$candidateRoot = Assert-ChildPath (Join-Path $projectRoot $definition.CandidateDirectory) $projectRoot 'Candidate directory'
$formalRoot = Assert-ChildPath (Join-Path $projectRoot $definition.FormalDirectory) $projectRoot 'Formal directory'
$documentSourceRoot = Join-Path $projectRoot $definition.PackageDocuments
$consumerDocumentsDirectoryName = -join @(0x6D88, 0x8D39, 0x6587, 0x6863 | ForEach-Object { [char]$_ })
$documentMirrorRoot = Assert-ChildPath `
    (Join-Path $dianaRoot "b-Office-OneHistory\$consumerDocumentsDirectoryName\$Module") `
    $dianaRoot `
    'Document mirror'
$moduleVersion = Read-ModuleVersion $versionPropsPath $definition.VersionProperty

# 模块的身份写在三处(版本源、源 manifest、快照 manifest)，这里对齐前两处。
# 宿主只有两处：版本源与快照 manifest，没有源 manifest 可对，也就少一处可漂移。
if ($definition.Kind -eq 'module') {
    if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
        throw "Source module manifest is missing: $sourceManifestPath"
    }
    $sourceManifest = [IO.File]::ReadAllText($sourceManifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($sourceManifest.name -ne $Module -or $sourceManifest.version -ne $moduleVersion) {
        throw "Source manifest must match $Module $moduleVersion before release"
    }
}

$sourceStatus = @(& git -C $projectRoot status --porcelain -- ':!b-Publish/**' ":!$($definition.FormalDirectory)/**")
if ($LASTEXITCODE -ne 0) { throw "Unable to read Git status: $projectRoot" }
$sourceDirty = $sourceStatus.Count -gt 0
if ($sourceDirty -and -not $AllowDirtySource) {
    throw "Source worktree is dirty. Commit it or explicitly pass -AllowDirtySource:`n$($sourceStatus -join [Environment]::NewLine)"
}
if ($sourceDirty) {
    Write-Warning "Publishing dirty $Module source because -AllowDirtySource was explicitly supplied."
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw "Unable to resolve source commit: $projectRoot"
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    Invoke-Checked 'powershell.exe' @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $definition.BuildScript),
        '-Configuration', 'Release', '-OutputRoot', $candidateRoot
    ) $projectRoot 'Build candidate package'
    Assert-ModuleSnapshot $candidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    if ($definition.Kind -eq 'module') {
        # 强制对齐：模块合同由 Diana 这一份执行，且不经注册表配置——
        # 模块无法跳过、替换或"因为本项目特殊"而改写规则。这是收口的约束点本身。
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $PSScriptRoot 'OneHistory.ModuleContract.ps1'),
            '-ProjectRoot', $projectRoot, '-Instantiation'
        ) $projectRoot 'Run aligned module contract'
        Invoke-ConfiguredModuleValidation @($definition.validation) $projectRoot
    } elseif ($definition.Kind -eq 'host') {
        # 宿主合同同样由 Diana 这一份执行；宿主专属门禁（冻结标签、版本源、UI 令牌）在其中。
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $PSScriptRoot 'OneHistory.HostContract.ps1'),
            '-ProjectRoot', $projectRoot, '-Instantiation'
        ) $projectRoot 'Run aligned host contract'
        Invoke-Checked 'dotnet.exe' @(
            'test', (Join-Path $projectRoot $definition.TestProject), '-c', 'Release', '--nologo',
            '--no-restore', '-p:NuGetAudit=false'
        ) $projectRoot 'Run host unit tests'
        foreach ($gate in @($definition.GateScripts)) {
            Invoke-Checked 'powershell.exe' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $gate)
            ) $projectRoot "Run host gate $(Split-Path -Leaf $gate)"
        }
    } else {
        throw "Unsupported publish kind: $($definition.Kind)"
    }

    $documentStage = Join-Path $workRoot 'consumer-docs'
    New-ConsumerDocumentMirror $documentSourceRoot $documentStage $Module $moduleVersion `
        $definition.ProjectDirectory $sourceCommit $sourceDirty
    Assert-ConsumerDocumentMirror $documentStage $Module $moduleVersion

    if (-not $Publish) {
        Write-Host "Verified candidate $Module ${moduleVersion}: $candidateRoot"
        Write-Host 'Pass -Publish to promote the candidate and consumer document mirror.'
        return
    }

    $formalStage = Join-Path $workRoot 'formal'
    Copy-Item -LiteralPath $candidateRoot -Destination $formalStage -Recurse
    Assert-ModuleSnapshot $formalStage $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    $oldVersion = 'none'
    $formalManifestPath = Join-Path $formalRoot $definition.SnapshotManifest
    if (Test-Path -LiteralPath $formalManifestPath -PathType Leaf) {
        $oldVersion = [string](([IO.File]::ReadAllText($formalManifestPath) | ConvertFrom-Json).version)
    }
    $formalBackup = Join-Path $projectRoot "b-Publish\history\$Module\$oldVersion-$stamp-$($transactionId.Substring(0, 8))"
    $documentBackup = Join-Path $dianaRoot "b-Publish\history\consumer-docs\$Module\$oldVersion-$stamp-$($transactionId.Substring(0, 8))"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $formalBackup), (Split-Path -Parent $documentBackup), (Split-Path -Parent $documentMirrorRoot) | Out-Null

    $formalBackedUp = $false
    $formalPromoted = $false
    $documentsBackedUp = $false
    $documentsPromoted = $false
    try {
        if (Test-Path -LiteralPath $formalRoot) {
            Move-Item -LiteralPath $formalRoot -Destination $formalBackup
            $formalBackedUp = $true
        }
        Move-Item -LiteralPath $formalStage -Destination $formalRoot
        $formalPromoted = $true
        Assert-ModuleSnapshot $formalRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

        if (Test-Path -LiteralPath $documentMirrorRoot) {
            Move-Item -LiteralPath $documentMirrorRoot -Destination $documentBackup
            $documentsBackedUp = $true
        }
        Move-Item -LiteralPath $documentStage -Destination $documentMirrorRoot
        $documentsPromoted = $true
        Assert-ConsumerDocumentMirror $documentMirrorRoot $Module $moduleVersion
    }
    catch {
        if ($documentsPromoted -and (Test-Path -LiteralPath $documentMirrorRoot)) {
            Move-Item -LiteralPath $documentMirrorRoot -Destination (Join-Path $workRoot 'failed-consumer-docs')
        }
        if ($documentsBackedUp -and (Test-Path -LiteralPath $documentBackup)) {
            Move-Item -LiteralPath $documentBackup -Destination $documentMirrorRoot
        }
        if ($formalPromoted -and (Test-Path -LiteralPath $formalRoot)) {
            Move-Item -LiteralPath $formalRoot -Destination (Join-Path $workRoot 'failed-formal')
        }
        if ($formalBackedUp -and (Test-Path -LiteralPath $formalBackup)) {
            Move-Item -LiteralPath $formalBackup -Destination $formalRoot
        }
        throw
    }

    Write-Host "Published $Module ${moduleVersion}: $formalRoot"
    Write-Host "Consumer documents: $documentMirrorRoot"
    if ($formalBackedUp) { Write-Host "Previous formal snapshot: $formalBackup" }

    Update-CommandManual
    if ($definition.Kind -eq 'host' -and $Module -eq 'HistoryVulcan') {
        Repair-VulcanAutostart $formalRoot
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
