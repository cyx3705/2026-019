# HistoryDiana — OneHistory 的 AI 工作区

HistoryDiana 是 OneHistory 的 **AI 侧常驻工作区**：托管跨项目公共定义，
并以 `diana` 域的指令提供工作树巡检、小工具、MCP 工具中继，以及按 z 通道查看已发布说明书。

与 HistoryMercury 对称——Mercury 是人的翻译官（活动坞、全局快捷键、命令工作台），
Diana 是 AI 的翻译官（公共定义、巡检、工具中继、文档通道）。Diana 不提供 UI 页面。

![OneHistory Logo](./Logo.png)

## 入口

| 入口 | 用途 |
| --- | --- |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令的机器可读清单 |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`b-Office-OneHistory/文档中心.md`](./b-Office-OneHistory/文档中心.md) | **跨项目公共文档中心**：OneHistory 定义、命名规范和 catalog 入口 |
| [`b-Office-Diana/`](./b-Office-Diana/) | Diana 自身的项目合同 |
| [`b-Code/Publish-OneHistoryModule.ps1`](./b-Code/Publish-OneHistoryModule.ps1) | 集中执行已登记模块的候选构建、测试、写入 z/docs 与正式提升 |
| [`b-Code/module-publish.manifest.json`](./b-Code/module-publish.manifest.json) | 普通模块的发布登记与验证步骤；新增普通模块无需修改主发布脚本 |

两个文档区分开的原因：公共区被**别的项目**消费，Diana 的合同只描述 Diana。

## 从这里开始

不熟悉 OneHistory 的结构时，按顺序读：

1. [OneHistory 总览](./b-Office-OneHistory/定义/OneHistory总览.md) —— 项目库 / 宿主 / 模块 /
   工作区 / 消费区 各指什么。
2. [目录与命名规范](./b-Office-OneHistory/定义/目录与命名规范.md) —— 指令三段式、
   版本单一来源、文档结构。
3. [OneHistory 文档中心](./b-Office-OneHistory/文档中心.md) —— 先执行 `diana.docs.catalog`。

## 指令

四类。除 `diana.relay.call` 外全部只读。`diana.docs.<域>` 的条数等于现场 `z-*` 文件夹数，不要写死总数。

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `project` | `diana.project.summary` / `recent` / `largest` | 已登记工作树的只读巡检 |
| `project` | `diana.project.manifest` / `docs` / `align` | 四个模块项目的 manifest、现行文档与对齐检查 |
| `kit` | `diana.kit.sha256` / `base64` / `guid` / `now` | 无副作用的小计算 |
| `relay` | `diana.relay.list` / `describe` / `call` | 按当前 MCP 策略实时列举与调用工具，绕开会话里的旧快照 |
| `docs` | `diana.docs.catalog` | 现场扫描全部 z 通道；跨项目读文档前必须先执行，把索引留在对话里 |
| `docs` | `diana.docs.<域>` | 一个 z 文件夹一条通道；省略 file 只列出，带 file 才读一篇 |

## 构建与验证

```bash
dotnet build ./b-Code-HistoryDiana/HistoryDiana.csproj -c Release
```

```bash
dotnet run --project ./b-Code-HistoryDiana/tests/HistoryDiana.Smoke/HistoryDiana.Smoke.csproj -c Release
```

## 部署

宿主的模块发现扫描各项目根下的 `z-*` 目录，因此发布到 [`z-HistoryDiana`](./z-HistoryDiana/)
即完成部署。模块发布不要关闭 Vulcan：宿主从内存加载 DLL，覆盖 z 后执行 `vulcan.module.reload`
（管线在正式宿主运行时会自己调）。只有替换宿主 EXE 才需要停进程。

当前开发版本是 `1.1.0`。运行中的宿主仍装载 z 里的旧版本，直到 Diana 自己也走一次正式提升。

## 集中发布

已登记四类项目：`HistoryJanus`、`HistoryMercury`、`HistoryMinerva`（Kind=module）与
`HistoryVulcan`（Kind=host）。普通模块的登记和验证步骤位于
[`b-Code/module-publish.manifest.json`](./b-Code/module-publish.manifest.json)，共用同一条管线；
Vulcan 保留宿主快照与门禁特例。候选构建的调用形状统一为
`-Configuration Release -OutputRoot <候选目录>`。

默认只生成并验证候选；正式提升必须显式加 `-Publish`。默认先部署后提交（允许脏工作树）；
只有要从干净 HEAD 复现时才加 `-RequireCleanSource`。发布成功后提交源码与该项目的 `z-*`。

```powershell
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryJanus
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryJanus -Publish
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryVulcan -Publish
```
