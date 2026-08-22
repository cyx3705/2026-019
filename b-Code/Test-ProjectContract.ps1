[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryDiana 自身的合同校验入口。
#
# 规则本体在宿主管线（HistoryVulcan eng/pipeline）。本文件只负责定位仓库根。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$clioRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$moduleContract = Join-Path $clioRoot '2026-023-HistoryVulcan\b-Code-HistoryVulcan\eng\pipeline\OneHistory.ModuleContract.ps1'
if (-not (Test-Path -LiteralPath $moduleContract -PathType Leaf)) {
    throw "找不到共用模块合同脚本: $moduleContract"
}

& $moduleContract `
    -ProjectRoot (Join-Path $PSScriptRoot '..') `
    -Instantiation:$Instantiation

exit $LASTEXITCODE
