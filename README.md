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
| `docs` | `diana.docs.catalog` / `read` / `audit` | 现查宿主指令目录：按域列出、渲染说明书、检查自描述缺陷 |
| `view` | `diana.view.windows` / `capture` | 列出并原尺寸捕获 HistoryVulcan 前端窗口 |

查别的模块怎么调用：先 `diana.docs.catalog`，再 `diana.docs.read domain=<域> [class=<类>]` 或 `command=<指令>`。2.8.0 起说明书就是注册自描述，没有消费文档。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office-Diana/现行约定.md) | 唯一的长期文档：真相在哪、取舍、真机才知道的、工程约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-HistoryDiana/` | 模块源码、manifest 与 `tests/` 下的 Smoke |
| `b-Code/` | 项目合同检查与候选构建辅助脚本 |
| `b-Office-Diana/` | `current/现行约定.md`，仅此一份 |
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
- 宿主 5.8.0 起不登记模块：本仓的发布描述在根 `project.manifest.json` 的 `publish` 节；
  `HistoryVulcan` 是宿主，走 `vulcan.release.cycle`，禁止走模块开发管线。
- `diana.project.docs` 只用于已授权项目的维护；查别的模块的指令一律走 `diana.docs.catalog/read`。

## 保留内容

### logo与作者

- 作者：pinavia
- ![logo](./Logo.png)

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。
