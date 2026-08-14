# OneHistory 大块定义

本文件定义 OneHistory 体系里每个"大块"是什么、边界在哪、谁拥有它。
遇到名词不确定时以本文件为准；本文件与某个项目的合同冲突时，先指出冲突再改，不要静默取一方。

## 一张图看清层次

```
OneHistory                      产品族名，也是品牌前缀 History* 的来源
└── HistoryClio                 项目库（每个编号目录是独立 git 仓）
    ├── 2026-019-HistoryDiana   项目
    ├── 2026-020-HistoryJanus   项目
    ├── 2026-021-HistoryMercury 项目
    ├── 2026-023-HistoryVulcan  项目（其产物是宿主）
    └── 2026-024-HistoryMinerva 项目
```

运行期是另一套层次，和目录层次**不是一回事**：

```
HistoryVulcan（宿主进程）
├── 指令总线 CommandBus / 注册表 CommandRegistry
├── 模块 HistoryJanus     ← 由 2026-020 的 z 快照装载
├── 模块 HistoryMercury   ← 由 2026-021 的 z 快照装载
├── 模块 HistoryMinerva   ← 由 2026-024 的 z 快照装载
└── 模块 HistoryDiana     ← 由 2026-019 的 z 快照装载
```

## 定义

### 项目库（HistoryClio）

每个编号项目目录是一个**独立 git 仓库**（目录内有 `.git` 文件夹），默认分支 `main`。
目录名就是项目身份。不再使用共享裸仓 `HistoryVesta.git` 或 worktree 指针文件。

- 路径：`C:\OneHistory\HistoryClio`
- 设置键：`proj.libraryroot`（旧 `proj.worktreeroot` 仅作一次性迁移；等于
  `C:\OneHistory\HistoryVesta` 时改写到 Clio）
- 模板项目：`0000-000-Template`（`proj.basebranch`）
- 已退役的共享库：`C:\OneHistory\HistoryVesta` 与 `HistoryVesta.git`。残留的
  `.git` 指针文件只诊断，不自动删除。

### 项目

一个独立仓 = 一个编号目录，命名 `<年>-<编号>-<名>`（例如 `2026-020-HistoryJanus`）。
项目是**版本与发布的单位**：每个项目有自己的版本源、自己的门禁、自己的 z 快照。

不是所有项目都产出模块。有的项目是纯资料或实验，不参与运行期。

### 宿主（HistoryVulcan）

唯一的运行进程与 UI 外壳，由项目 `2026-023-HistoryVulcan` 产出。

宿主提供、模块不得自造的东西：

- **指令总线与注册表**：所有能力都以指令形式登记到这里。
- **窗口与停靠**：模块通过 `IShellUiRegistrar` 注册页面，不自建窗口
  （桌面活动坞是唯一例外，它是脱离宿主窗口的桌面层）。
- **日志、设置、数据目录**：模块从 `IModuleContext` 取，不自己决定路径。
- **全局快捷键宿主**：4.1.0 起实现搬到 Mercury，但契约仍属宿主 Core。
- **MCP 暴露策略**：哪些指令能被远程调用由宿主统一裁决。

宿主有两个进程形态：**Shell**（有 UI）与 **ServiceHost**（无 UI）。
模块指令注册在服务进程，Shell 侧经总线的远程执行器透明转发。
**排查模块装载问题时要看服务进程的日志**，Shell 日志里通常什么都没有。

- Shell 日志：`%APPDATA%\HistoryVulcan\logs\`
- 服务日志：`%APPDATA%\HistoryVulcan\service\logs\`

### 模块（HistoryXX）

宿主装载的一个 DLL，提供一组指令，可选提供页面。

- 模块名保留 `History` 品牌前缀：`HistoryJanus`、`HistoryMercury`、`HistoryDiana`。
- **指令域去掉品牌前缀**：`janus`、`mercury`、`diana`。品牌前缀对每个模块都一样，
  出现在每条指令里没有信息量（DEC-023）。归一化真值是
  `HistoryVulcan.Core.Commands.ModuleDomainNaming.ToDomain(moduleName)`。
- 模块身份必须三处一致：`module.manifest.json` 的 `name`/`version`、
  `ModuleInfo` 类的同名属性、程序集版本。**任意两处不一致，宿主会静默跳过整个模块**，
  只在服务日志留一行 `[Warn] [module.discovery]`。
  正确做法是让 `ModuleInfo.Version` 从程序集读取，不写字面量。

### 工作区 / 消费区（z-*）

`z-<名>` 是项目根下的**跨项目消费目录**，保存该项目对外的正式快照。
宿主的模块发现只扫描各项目下的 `z-*` 目录，因此
**发布到 z 就等于部署**（Janus 例外，它另外还要同步到宿主的模块槽）。

- `z-HistoryVulcan`：宿主自身的正式快照，`host/` 下是可执行文件，已发布说明书在 `docs/`。
- `z-HistoryJanus` / `z-HistoryMercury` / `z-HistoryDiana` / `z-HistoryMinerva`：各模块的正式快照，
  运行文件为 `<模块>.dll`、`<模块>.xml`、`module.manifest.json`、`SHA256SUMS`，已发布 Markdown 在 `docs/`。
- 同名模块出现在多个 z 目录时，**所有同名候选一起被跳过**，不是取其一。
- 跨项目读说明书：先执行 `diana.docs.catalog` 把索引留在对话中，再选一条 `diana.docs.<域>`。

### 体系边界：哪些项目不属于 OneHistory

项目库 `HistoryClio` 里有很多编号项目，但**只有下列项目属于 OneHistory 体系**、
需要跟进宿主契约与跨项目规范：

| 项目 | 角色 |
| --- | --- |
| 2026-023-HistoryVulcan | 宿主 |
| 2026-019-HistoryDiana | AI 工作区 |
| 2026-020-HistoryJanus | 模块 |
| 2026-021-HistoryMercury | 模块 |
| 2026-024-HistoryMinerva | 模块 |

**WBall（2026-022）已独立发展，不再跟进 OneHistory 的任何改动。**
它虽然在同一个项目库里、也曾消费宿主，但自此不参与指令规范、版本对齐、
集中发布与门禁；宿主侧的破坏性变更**不需要**为它保留兼容路径，
评估影响面时也不必把它算进去。不要再在决策、影响面或待办里提到它。

其余编号项目（各类课程设计、实验项目等）从来就不在体系内，同理。

### 项目内目录前缀

| 前缀 | 含义 | 是否随项目发布 |
| --- | --- | --- |
| `a-` | 子项目 | 否 |
| `b-` | 项目组件（代码、文档、发布中间区） | 否 |
| `z-` | 跨项目消费快照 | 是 |

其余名字（`artifacts`、`Unused`、`eng` 等）必须在 `project.manifest.json` 的
`allowedRootDirectoryNames` 里显式登记，否则视为非法目录。

### AI 工作区（HistoryDiana）

本项目。定位是 **AI 侧的常驻工作区与工具箱**，与 Mercury 的定位对称：

- **Mercury 是人的翻译官**：活动坞、全局快捷键、命令工作台，让人快速驱动系统。
- **Diana 是 AI 的翻译官**：公共文档区、工作树巡检、MCP 工具中继、
  以及后续的可复用发布脚本与文档查看工具，让 AI 快速看清并操作整个体系。

Diana 不进入用户的日常界面，`ui: false`，只提供指令与文档。
