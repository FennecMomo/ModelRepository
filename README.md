# 模块仓库

为开发者与 Agent 提供可检索、可授权、可更新的功能模块。

第一版采用独立后端管理模块分类、权限与最新内容。本地接入工具将模块写入指定项目目录；项目自己的 Git 和用户使用的 Agent 负责历史、差异分析、恢复及更新后的验证。

## 需求与架构

- [第一版需求与整体架构](docs/v1-requirements-architecture.md)
- [管理与发布使用说明](docs/stage-a-usage.md)
- [阶段 A API](docs/stage-a-api.md)
- [阶段 A 验证记录](docs/stage-a-validation.md)

## 当前实现：0.1.1 / 阶段 A

已实现模块管理、分类标签、账号与模块权限、草稿上传、当前内容发布、授权下载和响应式 Web 界面。服务端采用 ASP.NET Core 10 + SQLite，文件按内容哈希存储。

```powershell
pwsh -File .\scripts\start.ps1
```

访问 <http://localhost:5188> 即可管理。默认自用模式不要求初始化、登录、密码、访问令牌或 CSRF。默认数据位于 `.local/data`，不进入 Git；已有账号和模块数据保留。

账号模式作为可选功能保留，只有设置 `Repository:RequireLogin=true` 才启用。账号支持中文，密码允许留空或使用简单数字，没有登录限流，也没有首次初始化必须使用 localhost 的限制。

```powershell
dotnet test ModelRepository.sln -c Release
```

本地项目目录覆盖、项目安装记录、CLI/MCP 和复用 Skill 尚未实现，属于阶段 B/C。当前版本只保留模块最新内容，不提供旧版源码备份、下载或回退。
