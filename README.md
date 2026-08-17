# HistoryDiana — OneHistory 的 AI 工作区

HistoryDiana 是 OneHistory 的 **AI 侧常驻工作区与模块开发管线**：AI 长期只在 Diana 管理的
`F:\ai工作区` 分支工作树中修改模块，模块仓库主线只作为基线、合并目标和正式发布来源；跨项目事实通过 Diana MCP 从正式 z 读取，
并以 `diana` 域提供隔离工作树、候选构建与试用、正式发布、项目巡检、MCP 中继和 z 文档通道。

与 HistoryMercury 对称——Mercury 是人的翻译官（活动坞、全局快捷键、命令工作台），
Diana 是 AI 的翻译官（隔离开发、候选验证、发布编排、巡检、中继和文档通道）。
Diana 不提供 UI 页面。

![OneHistory Logo](./Logo.png)

## 入口

| 入口 | 用途 |
| --- | --- |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令的机器可读清单 |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`b-Office-Diana/`](./b-Office-Diana/) | Diana 自身的项目合同 |
| [`b-Office-Diana/package/模块开发手册.md`](./b-Office-Diana/package/模块开发手册.md) | 新 AI 开发 OneHistory 模块时的必读流程 |
| [`b-Code/Publish-OneHistoryModule.ps1`](./b-Code/Publish-OneHistoryModule.ps1) | 集中执行已登记模块的候选构建、测试、写入 z/docs 与正式提升 |
| [`b-Code/module-publish.manifest.json`](./b-Code/module-publish.manifest.json) | 普通模块的发布登记与验证步骤；新增普通模块无需修改主发布脚本 |

模块源码和消费文档仍由各模块仓库拥有，Diana 只提供统一工作区、发布和 MCP 读取入口。

## 从这里开始

不熟悉 OneHistory 的结构时，按顺序读：

1. 要开发模块，先读[模块开发手册](./b-Office-Diana/package/模块开发手册.md)，按其中的
   `docs → worktree.create → release.cycle →（继续开发或 worktree.merge）` 流程执行。
2. 需要跨项目说明书时，先执行 `diana.docs.catalog`，把索引留在对话中，再读取对应 z 通道；
   不打开其他模块 worktree，也不从 Diana 本地目录寻找副本。

## 指令

七类。当前固定 24 条命令，另按现场 z 动态登记 `diana.docs.<域>`；不要写死动态通道总数。
命令按描述符投影为 MCP：只读命令是 `readonly`，六条显式写命令是 `standard`。

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `project` | `diana.project.summary` / `recent` / `largest` | 已登记工作树的只读巡检 |
| `project` | `diana.project.manifest` / `docs` / `align` | 四个模块项目的 manifest、现行文档与对齐检查 |
| `kit` | `diana.kit.sha256` / `base64` / `guid` / `now` | 无副作用的小计算 |
| `relay` | `diana.relay.list` / `describe` / `call` | 按当前 MCP 策略实时列举与调用工具，绕开会话里的旧快照 |
| `docs` | `diana.docs.catalog` | 现场扫描全部 z 通道；跨项目读文档前必须先执行，把索引留在对话里 |
| `docs` | `diana.docs.<域>` | 一个 z 文件夹一条通道；省略 file 只列出，带 file 才读一篇 |
| `worktree` | `diana.worktree.root` / `create` / `list` / `merge` | 建立、查看 AI 隔离工作树，或并回 main 并回收 |
| `release` | `diana.release.modules` / `status` / `log` / `cycle` | cycle 跑完门禁并提交；status/log 只读排障 |
| `trial` | `diana.trial.load` | 把候选或历史包交给 Vulcan 热重载（`vulcan.module.install`） |

六条 `standard` 写命令是 `relay.call`、`release.cycle`、`trial.load`、
`worktree.root/create/merge`；其余命令均为 `readonly`。测试和部署都只调同一热重载接口：
`vulcan.module.install` 校验完整包、原子替换 AppData 运行槽并扫描注册。测试不通过时，手动选择主树候选
或 `z-Publish/history/HistoryX-vX.Y.Z` 再热重载；不自动恢复。

## 构建与验证

```bash
dotnet build ./b-Code-HistoryDiana/HistoryDiana.csproj -c Release
```

```bash
dotnet run --project ./b-Code-HistoryDiana/tests/HistoryDiana.Smoke/HistoryDiana.Smoke.csproj -c Release
```

## 部署

宿主只扫描 `%AppData%\HistoryVulcan\Modules`。发布器先在项目 [`z-Publish`](./z-Publish/)
生成并验证版本化候选 `HistoryX-vX.Y.Z/`，再由 `diana.release.cycle` / `diana.trial.load`
调用本机 `vulcan.module.install` 热重载（与模块页「热重载」同一接口）；Diana 文档 MCP
只读取该候选中的 `docs/`。`vulcan.module.reload` 只重扫运行区，不再扫描项目目录。
（管线在正式宿主运行时会自己调）。只有替换宿主 EXE 才需要停进程。

当前源码版本为 `1.10.15`；运行区版本以 Vulcan 返回的模块 revision 为准。

## 集中发布

已登记 `HistoryDiana`、`HistoryJanus`、`HistoryMercury`、`HistoryMinerva`（Kind=module）；
`HistoryVulcan` 是发布器内置的 Kind=host 特例。普通模块的登记和验证步骤位于
[`b-Code/module-publish.manifest.json`](./b-Code/module-publish.manifest.json)，共用同一条管线；
Vulcan 保留宿主快照与门禁特例。候选构建的调用形状统一为
`-Configuration Release -OutputRoot <候选目录>`。

默认只生成并验证候选；正式提升必须显式加 `-Publish`。默认先部署后提交（允许脏工作树）；
只有要从干净 HEAD 复现时才加 `-RequireCleanSource`。发布成功后提交源码与该项目的 `z-Publish`。

```powershell
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryJanus
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryJanus -Publish
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryVulcan -Publish
```
