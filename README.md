# HistoryDiana — OneHistory 的 AI 工具区

HistoryDiana 是 OneHistory 的 **AI 工具区**：统一承接面向 AI 的跨项目观察、巡检、读取、转换和工具中继能力。
当前工具包括前端图形查看、项目巡检、小工具、MCP 中继和 z 文档通道；后续新增的 AI 通用工具也默认归 Diana。

业务模块仍拥有自己的领域能力，Diana 通过中继帮助 AI 发现和调用，不复制领域实现。唯一例外是已经冻结的
模块开发管线：`vulcan.dev.start/submit/finish` 继续由 HistoryVulcan 拥有，只走 Console CLI，不进入 Diana 或 MCP。
Diana 不提供自己的 UI 页面；`diana.view.*` 只观察现有 HistoryVulcan 前端。

![OneHistory Logo](./Logo.png)

## 入口

| 入口 | 用途 |
| --- | --- |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令的机器可读清单 |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`b-Office-Diana/`](./b-Office-Diana/) | Diana 自身的项目合同 |
| HistoryVulcan `b-Office/package/模块开发手册.md` | 模块开发工作区、送审和并回流程 |

## 从这里开始

1. AI 需要通用工具时，先从 `diana.*` 查找；新能力若不属于某个业务模块的领域合同，默认在 Diana 实现。
2. 需要跨项目说明书时，先执行 `diana.docs.catalog`，把索引留在对话中，再读取对应 z 通道。
3. 只有开发模块时例外：读宿主《模块开发手册》，通过 Console CLI 走
   `vulcan.dev.start` → `vulcan.dev.submit` → `vulcan.dev.finish`；宿主自身禁止走这三条。

## 指令

五类。文档查看使用固定的 `diana.docs.read`，每次按现场 z 动态解析模块域。
`standard` 写命令是 `diana.relay.call` 与写入运行态 PNG 的 `diana.view.capture`。

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `project` | `diana.project.summary` / `recent` / `largest` | 已登记工作树的只读巡检 |
| `project` | `diana.project.manifest` / `docs` / `align` | 当前已实现范围内四个项目的 manifest、现行文档与对齐检查 |
| `kit` | `diana.kit.sha256` / `base64` / `guid` / `now` | 无副作用的小计算 |
| `relay` | `diana.relay.list` / `describe` / `call` | 按当前 MCP 策略实时列举与调用工具 |
| `docs` | `diana.docs.catalog` | 现场扫描全部 z 通道 |
| `docs` | `diana.docs.read domain=<域>` | 按当前 z 快照读取任意模块文档 |
| `view` | `diana.view.windows` | 列出可捕获的 HistoryVulcan 前端窗口 |
| `view` | `diana.view.capture [handle=]` | 原尺寸捕获前端客户区，返回 PNG 路径、尺寸、哈希和像素统计 |

## 构建与验证

```bash
dotnet build ./b-Code-HistoryDiana/HistoryDiana.csproj -c Release
```

```bash
dotnet run --project ./b-Code-HistoryDiana/tests/HistoryDiana.Smoke/HistoryDiana.Smoke.csproj -c Release
```

## 部署

宿主只扫描 `%AppData%\HistoryVulcan\Modules`。候选提交、安装与热重载统一由宿主模块开发管线执行。

当前源码版本为 `2.3.0`，适配 HistoryVulcan `5.1.2`。

## 开发管线例外

Diana 不拥有发布器，也不登记 `diana.worktree.*`、`diana.release.*` 或 `diana.trial.*`。
已冻结的模块开发管线及 z 写入由 HistoryVulcan 独占；当前登记的模块包括 `HistoryDiana`、
`HistoryJanus`、`HistoryMercury`、`HistoryMinerva`、`HistoryAurora` 和 `HistoryPortunus`。
