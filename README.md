# HistoryDiana

> OneHistory 的 AI 工具区：观察、巡检、文档读取与 MCP 中继

## 定位

HistoryDiana 是 OneHistory 的 **AI 工具区**：统一承接面向 AI 的跨项目观察、巡检、读取、转换和工具中继能力。
新增的 AI 通用工具，只要不属于某个业务模块的领域合同，默认归 Diana。

- 业务模块仍拥有自己的领域能力；Diana 只中继调用，不复制、不接管领域实现。
- 不提供自己的 UI 页面；`diana.view.*` 只观察现有 HistoryVulcan 前端。
- 不拥有模块开发管线：`vulcan.dev.start/submit/finish` 由 HistoryVulcan 独占，只走 Console CLI，不进入 Diana 或 MCP。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-019` |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `diana` |
| 界面 | 无（`ui: false`） |
| MCP 投影 | `standard`；写命令只有 `diana.relay.call` 与 `diana.view.capture` |
| 版本与宿主下限 | [`HistoryDianaVersion.props`](./b-Code-HistoryDiana/HistoryDianaVersion.props) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `log` | `diana.log.read` | 读取前端控制台内存日志；默认只返回 Error/Fatal |
| `host` | `diana.host.modules` / `ready` / `observe` | 只读转发活宿主装载状态，`observe` 可对比基线 |
| `project` | `diana.project.summary` / `recent` / `largest` | 已登记工作树的只读巡检 |
| `project` | `diana.project.manifest` / `docs` / `align` | 项目 manifest、现行文档与入口对齐检查 |
| `kit` | `diana.kit.sha256` / `base64` / `guid` / `now` | 无副作用的小计算 |
| `relay` | `diana.relay.list` / `describe` / `call` | 按当前 MCP 策略列举与调用工具 |
| `docs` | `diana.docs.catalog` / `read` | 现场扫描 z 通道，按模块域读取已发布文档 |
| `view` | `diana.view.windows` / `capture` | 列出并原尺寸捕获 HistoryVulcan 前端窗口 |

跨项目读文档：先 `diana.docs.catalog`，再 `diana.docs.read domain=<域>`。完整参数与返回见 [模块 API](./b-Office-Diana/package/模块API.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office-Diana/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office-Diana/current/项目概览.md) | 目标、范围与状态 |
| [技术合同](./b-Office-Diana/current/技术合同.md) | 现行需求与架构 |
| [有效决策](./b-Office-Diana/current/有效决策.md) | 仍然有效的关键决策 |
| [验证合同](./b-Office-Diana/current/验证合同.md) | 验证层级、命令与证据 |
| [模块 API](./b-Office-Diana/package/模块API.md) | 跨模块消费合同 |
| [模块开发观察手册](./b-Office-Diana/package/模块开发观察手册.md) | 开发任一模块时，一轮里在哪三处用 Diana 取证 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-HistoryDiana/` | 模块源码、manifest 与 `tests/` 下的 Smoke |
| `b-Code/` | 项目合同检查与候选构建辅助脚本 |
| `b-Office-Diana/` | 项目文档：`current/` 现行合同、`package/` 消费合同、`history/` 只读归档 |
| `b-References/` | 参考图 |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 构建与验证

```powershell
dotnet restore .\b-Code-HistoryDiana\HistoryDiana.csproj
dotnet build .\b-Code-HistoryDiana\HistoryDiana.csproj -c Release
dotnet run --project .\b-Code-HistoryDiana\tests\HistoryDiana.Smoke\HistoryDiana.Smoke.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
宿主只扫描 `%AppData%\HistoryVulcan\Modules`，本仓不自行发布。

## 要点

- Diana 不登记 `diana.worktree.*`、`diana.release.*`、`diana.trial.*`，也不经 `diana.relay.call` 绕行开发管线。
- 模块发布登记表在宿主仓 `2026-023-HistoryVulcan/b-Code-Eng/pipeline/module-publish.manifest.json`；
  `HistoryVulcan` 是 `kind=host`，走 `vulcan.release.cycle`，禁止走模块开发管线。
- `diana.project.docs` 只用于已授权项目的维护；跨项目消费一律走 `diana.docs.catalog/read`。

## 保留内容
- 本模板项目介绍：此为最初的准备的项目模板
    每个分支项目都会由他去继承
- 作者：Pinavia - 2025

![logo](./Logo.png)
