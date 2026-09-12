[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryDiana 自身的只读项目合同检查。Vulcan 5.1 已将旧的跨仓
# OneHistory.ModuleContract.ps1 收回到宿主进程内管线；这里不再依赖主树源码路径。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$errors = [Collections.Generic.List[string]]::new()

function Require-File([string]$RelativePath) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $RelativePath) -PathType Leaf)) {
        $errors.Add("缺少必需文件: $RelativePath")
    }
}

function Require-Directory([string]$RelativePath) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $RelativePath) -PathType Container)) {
        $errors.Add("缺少必需目录: $RelativePath")
    }
}

try {
    $manifestPath = Join-Path $repoRoot 'project.manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "无法读取 project.manifest.json: $($_.Exception.Message)"
}

foreach ($field in @('id', 'name', 'title', 'status', 'version', 'branch')) {
    if ([string]::IsNullOrWhiteSpace([string]$manifest.project.$field)) {
        $errors.Add("project.manifest.json 缺少 project.$field")
    }
}

if ($manifest.project.title -ne 'HistoryDiana AI 工具区') {
    $errors.Add('project.title 必须为 HistoryDiana AI 工具区')
}

$positioningDocuments = @(
    'README.md',
    'AGENTS.md',
    'b-Office-Diana/current/项目概览.md',
    'b-Office-Diana/current/技术合同.md',
    'b-Office-Diana/current/有效决策.md',
    'b-Office-Diana/current/验证合同.md',
    'b-Office-Diana/文档中心.md',
    'b-Office-Diana/package/模块API.md'
)
foreach ($relativePath in $positioningDocuments) {
    $content = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw -Encoding UTF8
    if ($content -notmatch 'AI 工具区') {
        $errors.Add("现行入口未声明 AI 工具区定位: $relativePath")
    }
}

$pipelineBoundaryDocuments = @(
    'README.md',
    'AGENTS.md',
    'b-Office-Diana/current/项目概览.md',
    'b-Office-Diana/current/技术合同.md',
    'b-Office-Diana/package/模块API.md'
)
foreach ($relativePath in $pipelineBoundaryDocuments) {
    $content = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw -Encoding UTF8
    if ($content -notmatch 'vulcan\.dev\.start' -or $content -notmatch 'Console CLI') {
        $errors.Add("现行入口未声明冻结开发管线的 CLI 边界: $relativePath")
    }
}

$currentPositioningText = @(
    'README.md',
    'b-Office-Diana/current/项目概览.md',
    'b-Office-Diana/current/技术合同.md',
    'b-Office-Diana/current/验证合同.md',
    'b-Office-Diana/package/模块API.md'
) | ForEach-Object {
    Get-Content -LiteralPath (Join-Path $repoRoot $_) -Raw -Encoding UTF8
}
$currentPositioningText = $currentPositioningText -join "`n"
foreach ($obsoleteText in @('AI 侧常驻工作区', '开发闭环走宿主 MCP', 'HistoryAurora 当前不在 Diana 模块登记表')) {
    if ($currentPositioningText.Contains($obsoleteText)) {
        $errors.Add("现行合同仍含过时定位: $obsoleteText")
    }
}

foreach ($file in @($manifest.contract.requiredFiles)) { Require-File $file }
foreach ($directory in @($manifest.paths.activeRoots)) { Require-Directory $directory }

$versionProps = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'b-Code-HistoryDiana\HistoryDianaVersion.props') -Raw -Encoding UTF8)
$moduleVersion = [string]$versionProps.Project.PropertyGroup.HistoryDianaVersion
$moduleManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'b-Code-HistoryDiana\module.manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.project.version -ne $moduleVersion -or $moduleManifest.version -ne $moduleVersion) {
    $errors.Add('项目 manifest、版本 props 与模块 manifest 的版本必须一致')
}
if ($moduleManifest.name -ne 'HistoryDiana' -or $moduleManifest.type -ne 'HistoryVulcan.Module') {
    $errors.Add('模块 manifest 身份无效')
}
if ($moduleManifest.description -notmatch 'AI 工具区') {
    $errors.Add('模块 manifest 描述未声明 AI 工具区定位')
}
$moduleInfoSource = Get-Content -LiteralPath (Join-Path $repoRoot 'b-Code-HistoryDiana\ModuleInfo.cs') -Raw -Encoding UTF8
if ($moduleInfoSource -notmatch 'AI 工具区') {
    $errors.Add('ModuleInfo 运行时描述未声明 AI 工具区定位')
}

$hostDependency = @($manifest.externalDependencies | Where-Object { $_.name -eq 'HistoryVulcan' })
if ($hostDependency.Count -ne 1 -or $hostDependency[0].version -ne '5.1.2') {
    $errors.Add('HistoryVulcan 依赖必须唯一且固定为 5.1.2')
}

$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Code-HistoryDiana') -Recurse -File -Filter '*.cs'
foreach ($source in $sourceFiles) {
    $content = Get-Content -LiteralPath $source.FullName -Raw -Encoding UTF8
    if ($content -match 'context\.Settings|context\.Log|context\.DataDirectory') {
        $errors.Add("模块不得读取已移除的 IModuleContext 成员: $($source.FullName)")
    }
}

$projectFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.csproj'
foreach ($projectFile in $projectFiles) {
    $content = Get-Content -LiteralPath $projectFile.FullName -Raw -Encoding UTF8
    if ($content -match '<ProjectReference[^>]+(?:\.\.\\|\.\./)2026-') {
        $errors.Add("不得跨仓 ProjectReference: $($projectFile.FullName)")
    }
}

if ($Instantiation) {
    $textFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.md', '.json', '.props', '.csproj') } |
        Where-Object { $_.FullName -notmatch '\\(?:bin|obj|z-Publish|b-References)\\' -and $_.Name -ne 'AGENTS.md' }
    foreach ($file in $textFiles) {
        if ((Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8) -match '\{\{.+?\}\}') {
            $errors.Add("存在未实例化占位符: $($file.FullName)")
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "HistoryDiana project contract: PASS ($moduleVersion; Vulcan 5.1.2)"
