# HistoryDiana — OneHistory 的 AI 工作区

HistoryDiana 是 OneHistory 的 **AI 侧常驻工作区**：托管跨项目公共文档区，
并以 `diana` 域的指令提供工作树巡检、小工具与 MCP 工具中继。

与 HistoryMercury 对称——Mercury 是人的翻译官（活动坞、全局快捷键、命令工作台），
Diana 是 AI 的翻译官（公共文档、巡检、工具中继）。Diana 不提供 UI 页面。

![OneHistory Logo](./Logo.png)

## 入口

| 入口 | 用途 |
| --- | --- |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令的机器可读清单 |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`b-Office-OneHistory/文档中心.md`](./b-Office-OneHistory/文档中心.md) | **跨项目公共文档中心**：OneHistory 定义、命名规范和消费文档入口 |
| [`b-Office-Diana/`](./b-Office-Diana/) | Diana 自身的项目合同 |
| [`b-Code/Publish-OneHistoryModule.ps1`](./b-Code/Publish-OneHistoryModule.ps1) | 集中执行已登记模块的候选构建、测试、正式提升和消费文档镜像 |
| [`b-Code/module-publish.manifest.json`](./b-Code/module-publish.manifest.json) | 普通模块的发布登记与验证步骤；新增普通模块无需修改主发布脚本 |

两个文档区分开的原因：公共区被**别的项目**消费，Diana 的合同只描述 Diana。

## 从这里开始

不熟悉 OneHistory 的结构时，按顺序读：

1. [OneHistory 总览](./b-Office-OneHistory/定义/OneHistory总览.md) —— 项目库 / 宿主 / 模块 /
   工作区 / 消费区 各指什么。
2. [目录与命名规范](./b-Office-OneHistory/定义/目录与命名规范.md) —— 指令三段式、
   版本单一来源、文档结构。
3. [OneHistory 文档中心](./b-Office-OneHistory/文档中心.md) —— 要接入某个模块或查公共定义时从这里跳转。

## 指令

13 条，三类，除 `diana.relay.call` 外全部只读；其中 `diana.project.*` 还提供四个模块项目的
manifest、现行文档和对齐检查。

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `project` | `diana.project.summary` / `recent` / `largest` | 已登记工作树的只读巡检 |
| `project` | `diana.project.manifest` / `docs` / `align` | 四个模块项目的 manifest、现行文档与对齐检查 |
| `kit` | `diana.kit.sha256` / `base64` / `guid` / `now` | 无副作用的小计算 |
| `relay` | `diana.relay.list` / `describe` / `call` | 按当前 MCP 策略实时列举与调用工具，绕开会话里的旧快照 |

## 构建与验证

```bash
dotnet build ./b-Code-HistoryDiana/HistoryDiana.csproj -c Release
```

```bash
dotnet run --project ./b-Code-HistoryDiana/tests/HistoryDiana.Smoke/HistoryDiana.Smoke.csproj -c Release
```

## 部署

宿主的模块发现扫描各项目根下的 `z-*` 目录，因此发布到 [`z-HistoryDiana`](./z-HistoryDiana/)
即完成部署，无需拷贝到宿主模块槽。部署后重启宿主或执行 `vulcan.module.reload`。

## 集中发布

已登记四类项目：`HistoryJanus`、`HistoryMercury`、`HistoryMinerva`（Kind=module）与
`HistoryVulcan`（Kind=host）。普通模块的登记和验证步骤位于
[`b-Code/module-publish.manifest.json`](./b-Code/module-publish.manifest.json)，共用同一条管线；
Vulcan 保留宿主快照与门禁特例。候选构建的调用形状统一为
`-Configuration Release -OutputRoot <候选目录>`。

默认只生成并验证候选；正式提升必须显式加 `-Publish`，脏工作树
还必须再加 `-AllowDirtySource`，且来源状态会写入 Diana 的消费文档镜像清单。

```powershell
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryJanus
.\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryVulcan -Publish
```

## 规划中

- 文档查看 MCP
- 条件成熟后把 AI 工作区整体迁入本项目
