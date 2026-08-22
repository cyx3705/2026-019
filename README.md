# HistoryDiana — OneHistory 的 AI 工作区

HistoryDiana 是 OneHistory 的 **AI 侧常驻工作区**：项目巡检、小工具、MCP 中继和 z 文档通道。
模块开发闭环已迁到宿主 `vulcan.worktree.*` / `vulcan.release.*`。

与 HistoryMercury 对称——Mercury 是人的翻译官，Diana 是 AI 的翻译官（巡检、中继和文档通道）。
Diana 不提供 UI 页面。

![OneHistory Logo](./Logo.png)

## 入口

| 入口 | 用途 |
| --- | --- |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令的机器可读清单 |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`b-Office-Diana/`](./b-Office-Diana/) | Diana 自身的项目合同 |
| HistoryVulcan `b-Office/package/模块开发手册.md` | 开工作树、cycle、并回主线 |
| HistoryVulcan `eng/pipeline/Publish-OneHistoryModule.ps1` | 由宿主 cycle 拉起的集中发布脚本 |

## 从这里开始

1. 要开发模块，读宿主《模块开发手册》，走 `vulcan.worktree.create` → `vulcan.release.cycle` →
   `vulcan.worktree.merge`。
2. 需要跨项目说明书时，先执行 `diana.docs.catalog`，把索引留在对话中，再读取对应 z 通道。

## 指令

四类。当前固定 14 条命令，另按现场 z 动态登记 `diana.docs.<域>`。
唯一 `standard` 写命令是 `diana.relay.call`。

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `project` | `diana.project.summary` / `recent` / `largest` | 已登记工作树的只读巡检 |
| `project` | `diana.project.manifest` / `docs` / `align` | 四个模块项目的 manifest、现行文档与对齐检查 |
| `kit` | `diana.kit.sha256` / `base64` / `guid` / `now` | 无副作用的小计算 |
| `relay` | `diana.relay.list` / `describe` / `call` | 按当前 MCP 策略实时列举与调用工具 |
| `docs` | `diana.docs.catalog` | 现场扫描全部 z 通道 |
| `docs` | `diana.docs.<域>` | 一个 z 文件夹一条通道 |

## 构建与验证

```bash
dotnet build ./b-Code-HistoryDiana/HistoryDiana.csproj -c Release
```

```bash
dotnet run --project ./b-Code-HistoryDiana/tests/HistoryDiana.Smoke/HistoryDiana.Smoke.csproj -c Release
```

## 部署

宿主只扫描 `%AppData%\HistoryVulcan\Modules`。正式更新走 `vulcan.release.cycle name=HistoryDiana`。

当前源码版本为 `2.0.1`。

## 集中发布

已登记 `HistoryDiana`、`HistoryJanus`、`HistoryMercury`、`HistoryMinerva`（Kind=module）；
`HistoryVulcan` 是发布器内置的 Kind=host 特例。登记表位于
[`b-Code/module-publish.manifest.json`](../2026-023-HistoryVulcan/b-Code-HistoryVulcan/eng/pipeline/module-publish.manifest.json)。
