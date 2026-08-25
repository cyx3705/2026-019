# AIReady 项目工具

本目录保存 HistoryDiana 的项目合同检查和候选构建辅助工具，属于活动源码根，不能删除。
它不承载 Diana 模块业务源码；模块实现和 Smoke 位于 `b-Code-HistoryDiana/`。

`Test-ProjectContract.ps1` 验证 manifest、活动路径、a/b/z 根目录规则、现行文档、本地 Markdown
链接和实例化占位符。脚本只读检查仓库，不提交、推送、发布或修改外部系统。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1
```

派生项目首次启用时运行严格模式：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```
