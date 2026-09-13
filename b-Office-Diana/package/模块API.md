# HistoryDiana 模块 API

模块版本：**2.6.0**；宿主：**HistoryVulcan 5.x**。

本文件是**总线面**合同。代码面在 `b-Office-Diana/current/技术合同.md`；
AI 面由 MCP 服务封装（工具名把 `.` 换成 `_`，如 `diana_docs_read`），本文件不重复。
文档读取顺序与目录规范在 `b-Office-Diana/文档中心.md`，不在这里。

## 这个模块提供什么

Diana 是 OneHistory 的 **AI 工具区**：跨项目观察、巡检、读取、转换与工具编排，
面向 AI 的通用能力默认在此实现。
指令域 `diana`，21 条，**除 `relay.call` 与 `view.capture` 外全部只读**，无隐藏指令。

边界：业务模块继续拥有自己的领域能力。Diana 可以中继调用，但**不复制、不接管**领域实现。
唯一例外是 HistoryVulcan 已冻结的 `vulcan.dev.start/submit/finish` 模块开发管线——
它只走 Console CLI，不属于 Diana，也不经 MCP 调用。

## 文档

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `diana.docs.catalog` | — | 列出全部 z 文档通道与文件名 |
| `diana.docs.read` | `domain`(必填)、`file`、`heading` | 按当前 z 文档目录读任意模块的 Markdown |

**跨项目读文档前必须先 `catalog`**，把索引留在对话里；新增模块不需要新增命令。
`file` 收 z 内相对路径或唯一文件名，省略则只列该模块文档；长文带 `heading` 只取一节，
免得整篇进对话。其他模块的已发布正文在各自 `z-*/docs/`，Diana 不保存副本。

全文与章节正文保留在 `Data.Content`，消息只给短摘要；版本和 SHA 字段保持不变。

## 项目

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `diana.project.manifest` | `name` | 身份、版本、活动目录与验证命令 |
| `diana.project.docs` | `name`、`kind` | 仅用于已授权项目维护，跨项目消费走 docs 通道；读现行文档；`kind` = `overview` / `technicalContract` / `decisions` / `verification` |
| `diana.project.summary` | `name`、`top`、`includeGenerated` | 文件、体积与一级目录热点 |
| `diana.project.recent` | `name`、`days`、`limit` | 最近修改的文件 |
| `diana.project.largest` | `name`、`limit`、`minMb` | 最大的文件 |
| `diana.project.align` | `name`(可选) | 指定项目或动态枚举库根带 manifest 的 Git 项目，仅检查身份与文档入口存在性 |

## 宿主与日志

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `diana.host.modules` | `name` | 活宿主装载的模块、版本、实例 ID、指令数与**附着失败原文** |
| `diana.host.ready` | — | 本轮装载是否全部接上 |
| `diana.host.observe` | `name`(必填)、`baseline`(可选) | 合并只读观察，返回基线令牌或与旧基线比较；不代表管线完成 |
| `diana.log.read` | `minlevel`、`source`、`keyword`、`after`、`limit`、时间 | 读当前前端控制台的结构化内存日志 |

**控制台故障先用 `diana.log.read`**：默认 `minlevel=error`，可按来源、关键字、时间或 `after`
序号增量查询。它**不改变控制台筛选器**，也不读磁盘日志。

`host.modules` / `host.ready` 只读转发宿主自己的装载状态，不改运行包，也不参与开发管线。

## MCP 工具中继

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `diana.relay.list` | `filter`、`modulesonly` | 实时列出当前 MCP 策略下可见的工具 |
| `diana.relay.describe` | `name` | 看一个工具的描述与 JSON Schema |
| `diana.relay.call` | `name`、`argumentsjson` | 按**当前**工具目录调用，不依赖会话里的旧快照 |

工具集体消失时先用 `relay.list` 核对现状，别照着旧快照重试。

## 前端图形查看

先 `diana.view.windows` 取窗口句柄，再 `diana.view.capture handle=<句柄>`；
省略 `handle` 自动选客户区面积最大的可见窗口。
结果给出原尺寸 PNG 的绝对路径、SHA-256、采集方式与像素统计——AI 应**读取那个 PNG**
核对布局、文字、重叠与空白区域。只捕获 `HistoryVulcan.exe`，不读其他桌面应用；最小化窗口明确拒绝。

## 小工具

`diana.kit.now`（本地时间与 Unix 秒）、`diana.kit.guid`、`diana.kit.sha256 text=`（十六进制大写）、
`diana.kit.base64 text=`。四条都是纯函数，只读。

## 开发模块时怎么用

开发任一 OneHistory 模块时读 [`模块开发观察手册.md`](./模块开发观察手册.md)：
一轮里的三个取证点（改之前取基线、`submit` 之后按判据核对、卡住时按症状对照），
以及 Diana 工具不可用时的降级顺序。流程本身以宿主 `b-Office/package/模块开发手册.md` 为准。
