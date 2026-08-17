[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Module,
    [switch]$Publish,
    [switch]$AllowDirtySource,
    [switch]$RequireCleanSource,
    # AI 工作区里的提交级验证：从指定工作树构建并跑门禁。正式 Clio z 的 -Publish
    # 永远只从主树来，因此本参数与 -Publish 互斥。无 -Publish 时，工作树把已验证
    # 候选写入该树自己的 z-*（宿主不扫描），供 diana.release.cycle 试用装载。
    # 不能叫 ProjectRoot：PowerShell 变量大小写不敏感，会和脚本内的 $projectRoot 撞成同一个，
    # 被后者覆盖后判断恒真，表现为主树构建也去传工作树参数。
    [string]$SourceWorktree
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dianaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectsRoot = [IO.Path]::GetFullPath((Join-Path $dianaRoot '..'))
$transactionId = [Guid]::NewGuid().ToString('N')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$workRoot = Join-Path ([IO.Path]::GetTempPath()) "OneHistory.Package\module-release-$transactionId"

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
        CandidateDirectory = 'z-Publish'
        FormalDirectory = 'z-Publish'
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

function Get-VulcanHostRoot {
    $path = Join-Path $projectsRoot '2026-023-HistoryVulcan\z-Publish'
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        return $null
    }
    return Assert-ChildPath $path $projectsRoot 'Vulcan host snapshot'
}

function Get-VulcanFormalExecutable {
    $moduleRoot = Get-VulcanHostRoot
    if ([string]::IsNullOrWhiteSpace($moduleRoot)) {
        return $null
    }
    $formalRoot = [IO.Path]::GetFullPath($moduleRoot).TrimEnd('\') + '\'
    $hostExecutable = [IO.Path]::GetFullPath((Join-Path $moduleRoot 'host\HistoryVulcan.exe'))
    if (-not $hostExecutable.StartsWith($formalRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "HistoryVulcan formal executable escaped the formal root: $hostExecutable"
    }
    return $hostExecutable
}

# 调用方一律写成 @(Get-VulcanFormalProcesses)：函数返回数组时 PowerShell 会把它摊进管线，
# 空数组因此摊成 $null，而 StrictMode 下 $null.Count 直接抛「找不到属性 Count」。
# 这条路径平时不走——模块发布时 Vulcan 通常开着——直到先促级宿主（管线会停掉它）
# 再促级模块，才第一次撞上。不要改成 `,@()` 包一层：那会让 @() 收集到「一个空数组」，
# Count 变成 1，宿主没运行也判成在运行，后面按进程对象用它就更难查了。
function Get-VulcanFormalProcesses {
    $hostExecutable = Get-VulcanFormalExecutable
    if ([string]::IsNullOrWhiteSpace($hostExecutable) -or
        -not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
        return @()
    }

    return @(Get-CimInstance Win32_Process -Filter "Name='HistoryVulcan.exe'" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
            [IO.Path]::GetFullPath([string]$_.ExecutablePath).Equals(
                $hostExecutable,
                [StringComparison]::OrdinalIgnoreCase)
        })
}

function Test-VulcanFormalProcessRunning {
    return @(Get-VulcanFormalProcesses).Count -gt 0
}

function Get-PublicApiBaselineEntries {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$SkipNullableEnable
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    return @(
        [IO.File]::ReadAllLines($Path, [Text.UTF8Encoding]::new($false)) |
            ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) } |
            Where-Object {
                ($_ -ne '') -and (-not $SkipNullableEnable -or $_ -ne '#nullable enable')
            }
    )
}

function Ensure-HostPublicApiBaseline {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $componentRoot = Join-Path $ProjectRoot 'b-Code-HistoryVulcan'
    $baselineRoot = Join-Path $componentRoot 'eng\public-api-baselines'
    $versionProps = Join-Path $componentRoot 'VulcanVersion.props'
    if (-not (Test-Path -LiteralPath $versionProps -PathType Leaf)) {
        throw "VulcanVersion.props is missing: $versionProps"
    }

    $versionMatch = [regex]::Match(
        [IO.File]::ReadAllText($versionProps),
        '<VulcanVersion>(?<version>[^<]+)</VulcanVersion>')
    if (-not $versionMatch.Success) {
        throw 'VulcanVersion.props does not contain VulcanVersion'
    }

    $version = $versionMatch.Groups['version'].Value.Trim()
    $currentDir = Join-Path $baselineRoot $version
    if (Test-Path -LiteralPath $currentDir -PathType Container) {
        return
    }

    if (-not (Test-Path -LiteralPath $baselineRoot -PathType Container)) {
        throw "Public API baseline root is missing: $baselineRoot"
    }

    $previous = @(
        Get-ChildItem -LiteralPath $baselineRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
            Where-Object { [version]$_.Name -lt [version]$version } |
            Sort-Object { [version]$_.Name }
    ) | Select-Object -Last 1
    if ($null -eq $previous) {
        throw "Missing approved Unshipped baseline directory for $version, and no previous version exists to inherit: $currentDir"
    }

    $projects = @(
        'HistoryVulcan.Core'
        'HistoryVulcan.Services'
        'HistoryVulcan.ServiceHost'
        'HistoryVulcan.Shell'
    )
    $changed = @()
    foreach ($project in $projects) {
        $current = Get-PublicApiBaselineEntries -Path (Join-Path $componentRoot "src\$project\PublicAPI.Unshipped.txt") -SkipNullableEnable
        $approved = Get-PublicApiBaselineEntries -Path (Join-Path $previous.FullName "$project.Unshipped.txt")
        if ($null -eq $current -or $null -eq $approved) {
            $changed += $project
            continue
        }

        $difference = @(Compare-Object -ReferenceObject @($approved) -DifferenceObject @($current))
        if ($difference.Count -ne 0) {
            $changed += $project
        }
    }

    if ($changed.Count -gt 0) {
        throw @"
Public API Unshipped differs from $($previous.Name) ($($changed -join ', ')). Do not auto-approve a new contract.
After review, copy the previous baseline then replace Unshipped files with the intended contract:
  Copy-Item -LiteralPath '$($previous.FullName)' -Destination '$currentDir' -Recurse
"@
    }

    Copy-Item -LiteralPath $previous.FullName -Destination $currentDir -Recurse
    Write-Host "Public API unchanged; inherited baseline $($previous.Name) -> $version"
}

function Stop-VulcanFormalProcesses {
    $hostExecutable = Get-VulcanFormalExecutable
    if ([string]::IsNullOrWhiteSpace($hostExecutable)) {
        Write-Host 'HistoryVulcan 正式宿主快照不存在，跳过停进程'
        return
    }
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $targets = @(Get-VulcanFormalProcesses)
        if ($targets.Count -eq 0) {
            Write-Host 'HistoryVulcan 正式宿主进程已停止，可安全提升快照'
            return
        }

        foreach ($target in $targets) {
            Stop-Process -Id $target.ProcessId -Force -ErrorAction Stop
        }
        Start-Sleep -Milliseconds 300
    }

    $remaining = @(Get-VulcanFormalProcesses)
    if ($remaining.Count -gt 0) {
        throw "HistoryVulcan 正式宿主仍在运行，拒绝移动正式快照：$($remaining.ProcessId -join ', ')"
    }
}

function Invoke-VulcanLiveCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [int]$TimeoutSeconds = 120
    )

    if (-not (Test-VulcanFormalProcessRunning)) {
        return $false
    }

    $endpointPath = Join-Path $env:APPDATA 'HistoryVulcan\service\endpoint.json'
    if (-not (Test-Path -LiteralPath $endpointPath -PathType Leaf)) {
        Write-Warning "正式宿主在运行，但找不到服务端点 $endpointPath，无法热重载"
        return $false
    }

    $endpoint = [IO.File]::ReadAllText($endpointPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    $port = [int]$endpoint.port
    $processId = [int]$endpoint.processId
    if ($port -lt 1024 -or $port -gt 65535) {
        Write-Warning "正式宿主端点端口无效：$port"
        return $false
    }
    if ($processId -gt 0) {
        $running = @(Get-VulcanFormalProcesses | Where-Object { $_.ProcessId -eq $processId })
        if ($running.Count -eq 0) {
            Write-Warning "endpoint.json 指向的服务进程 $processId 不是当前正式宿主，跳过热重载"
            return $false
        }
    }

    $uri = "http://127.0.0.1:$port/api/command"
    $headers = @{
        'X-HistoryVulcan-Client' = 'Shell'
        'X-Client-Name' = 'Publish-OneHistoryModule'
        'X-Session-Id' = [Guid]::NewGuid().ToString('N')
    }
    $body = (@{ text = $Text; source = 'publish' } | ConvertTo-Json -Compress)
    try {
        $response = Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType 'application/json; charset=utf-8' `
            -Headers $headers -TimeoutSec $TimeoutSeconds
    }
    catch {
        Write-Warning "向正式宿主发送 $Text 失败：$($_.Exception.Message)"
        return $false
    }

    $success = $false
    if ($null -ne $response.success) { $success = [bool]$response.success }
    elseif ($null -ne $response.Success) { $success = [bool]$response.Success }
    if (-not $success) {
        $message = [string]$response.message
        if ([string]::IsNullOrWhiteSpace($message)) { $message = [string]$response.Message }
        Write-Warning "正式宿主拒绝 $Text：$message"
        return $false
    }

    Write-Host "Live host: $Text"
    return $true
}

function Sync-FormalSnapshotInPlace {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$FormalRoot
    )

    $stagePrefix = [IO.Path]::GetFullPath($Stage).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Stage -File -Recurse)
    if ($files.Count -eq 0) {
        throw "In-place snapshot stage is empty: $Stage"
    }

    $stageKeys = @{}
    $checksum = $null
    foreach ($file in $files) {
        $key = $file.FullName.Substring($stagePrefix.Length)
        if ($file.Name -eq 'SHA256SUMS') {
            $checksum = $file
            continue
        }
        $stageKeys[$key] = $true
        $destination = Join-Path $FormalRoot $key
        $directory = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    if ($null -ne $checksum) {
        $key = $checksum.FullName.Substring($stagePrefix.Length)
        $stageKeys[$key] = $true
        Copy-Item -LiteralPath $checksum.FullName -Destination (Join-Path $FormalRoot $key) -Force
    }

    $formalPrefix = [IO.Path]::GetFullPath($FormalRoot).TrimEnd('\') + '\'
    foreach ($existing in @(Get-ChildItem -LiteralPath $FormalRoot -File -Recurse)) {
        $key = $existing.FullName.Substring($formalPrefix.Length)
        if (-not $stageKeys.ContainsKey($key)) {
            Remove-Item -LiteralPath $existing.FullName -Force
        }
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

function Get-ChecksumMarker {
    param([Parameter(Mandatory = $true)][string]$SumsPath)

    if (Test-Path -LiteralPath $SumsPath -PathType Leaf) {
        foreach ($line in [IO.File]::ReadAllLines($SumsPath, [Text.UTF8Encoding]::new($false))) {
            if ($line -match '^[0-9A-Fa-f]{64} \*') { return ' *' }
            if ($line -match '^[0-9A-Fa-f]{64}  ') { return '  ' }
        }
    }
    return '  '
}

function Write-SnapshotChecksums {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    $marker = Get-ChecksumMarker $sumsPath
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object Name -ne 'SHA256SUMS' |
        Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Snapshot has no files to checksum: $Root"
    }
    $lines = foreach ($file in $files) {
        $key = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        "$hash$marker$key"
    }
    [IO.File]::WriteAllLines($sumsPath, $lines, [Text.UTF8Encoding]::new($false))
}

function Merge-PackageDocumentsIntoSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$SnapshotRoot
    )

    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "Package document source is missing: $SourceRoot"
    }
    $documents = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.md' -File | Sort-Object Name)
    if ($documents.Count -eq 0) {
        throw "No consumer Markdown documents found: $SourceRoot"
    }

    $docsRoot = Join-Path $SnapshotRoot 'docs'
    New-Item -ItemType Directory -Force -Path $docsRoot | Out-Null
    foreach ($document in $documents) {
        Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $docsRoot $document.Name) -Force
    }
    Write-SnapshotChecksums $SnapshotRoot
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
if (-not [string]::IsNullOrWhiteSpace($SourceWorktree)) {
    if ($Publish) {
        throw '-SourceWorktree 只用于工作区门禁验证，不能与 -Publish 同用：正式促级必须从主树构建。'
    }
    $projectRoot = [IO.Path]::GetFullPath($SourceWorktree)
    if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
        throw "指定的工作树不存在: $projectRoot"
    }
    Write-Host "[$Module] 从工作树构建并验证，写入该树 z-*（不写正式 Clio z）: $projectRoot"
}
else {
    $projectRoot = Assert-ChildPath (Join-Path $projectsRoot $definition.ProjectDirectory) $projectsRoot 'Module project'
}
$versionPropsPath = Join-Path $projectRoot $definition.VersionProps
$sourceManifestPath = Join-Path $projectRoot $definition.SourceManifest
$candidateRoot = Assert-ChildPath (Join-Path $projectRoot $definition.CandidateDirectory) $projectRoot 'Candidate directory'
$formalRoot = Assert-ChildPath (Join-Path $projectRoot $definition.FormalDirectory) $projectRoot 'Formal directory'
$documentSourceRoot = Join-Path $projectRoot $definition.PackageDocuments
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

$sourceStatus = @(& git -C $projectRoot status --porcelain -- ':!z-Publish/**' ":!$($definition.FormalDirectory)/**")
if ($LASTEXITCODE -ne 0) { throw "Unable to read Git status: $projectRoot" }
$sourceDirty = $sourceStatus.Count -gt 0
if ($sourceDirty -and $RequireCleanSource -and -not $AllowDirtySource) {
    throw "Source worktree is dirty. Default is deploy-then-commit; pass -RequireCleanSource only when you need a clean HEAD, or omit it and commit source + z together after publish:`n$($sourceStatus -join [Environment]::NewLine)"
}
if ($sourceDirty) {
    Write-Warning "Publishing $Module from a dirty worktree. Commit source together with z after publish."
}
if ($AllowDirtySource) {
    Write-Warning "-AllowDirtySource is now the default deploy-then-commit path; the switch is kept only for old callers."
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw "Unable to resolve source commit: $projectRoot"
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    # 从工作树构建时，工作树在库根之外，构建脚本自己那条"同库根"的相对路径必然指空，
    # 因此把真实的宿主快照根显式传下去。主树构建时不传，脚本沿用原有缺省，行为不变。
    $buildArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $definition.BuildScript),
        '-Configuration', 'Release', '-OutputRoot', $candidateRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($SourceWorktree)) {
        # 逐处透传参数太脆：构建、门禁、验证各有各的调用点，漏一处就又是一次"撞了才发现"。
        # 改用环境变量，所有子进程一并继承；脚本侧在参数为空时回落到它。
        $hostSnapshot = Join-Path $projectsRoot '2026-023-HistoryVulcan\z-Publish'
        $env:HISTORYVULCAN_PACKAGE_ROOT = $hostSnapshot
        $buildArguments += @('-HistoryVulcanPackageRoot', $hostSnapshot)
    }
    Invoke-Checked 'powershell.exe' $buildArguments $projectRoot 'Build candidate package'
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
        Ensure-HostPublicApiBaseline -ProjectRoot $projectRoot
        foreach ($gate in @($definition.GateScripts)) {
            Invoke-Checked 'powershell.exe' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $gate)
            ) $projectRoot "Run host gate $(Split-Path -Leaf $gate)"
        }
    } else {
        throw "Unsupported publish kind: $($definition.Kind)"
    }

    Merge-PackageDocumentsIntoSnapshot $documentSourceRoot $candidateRoot
    Assert-ModuleSnapshot $candidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    if (-not $Publish) {
        if ($SourceWorktree) {
            Write-Host "Worktree candidate z-Publish is ready: $candidateRoot"
            Write-Host 'Vulcan runtime scans only AppData; diana.release.cycle will trial-load this verified root candidate.'
            return
        }

        Write-Host "Verified candidate $Module ${moduleVersion}: $candidateRoot"
        Write-Host 'Pass -Publish to run the release cycle; Vulcan installation is performed by the host command path.'
        return
    }
    Assert-ModuleSnapshot $candidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind
    Write-Host "Published candidate $Module ${moduleVersion}: $candidateRoot"
    Write-Host "Published documents: $(Join-Path $candidateRoot 'docs')"
    if ($definition.Kind -eq 'host' -and $Module -eq 'HistoryVulcan') {
        Repair-VulcanAutostart $candidateRoot
    }
    Write-Host "Deploy-then-commit: 提交 $projectRoot 的源码与 z-Publish 根候选；运行时安装由 vulcan.module.install 完成。"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
