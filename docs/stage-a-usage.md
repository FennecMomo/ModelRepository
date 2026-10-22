# 0.1.1 管理与发布使用说明

## 当前交付范围

已实现需求阶段 A：模块管理、分类标签、账号与模块授权、草稿文件、当前内容发布、授权下载和基础管理界面。附带只读访问令牌，供后续工具接入复用。

项目目录覆盖更新、本地安装记录、CLI/MCP 接入和复用 Skill 属于阶段 B/C，本次尚未实现。

## 本机启动

需要 .NET SDK 10.0.401 或同一 feature band 的更新补丁版本。

在仓库根目录运行：

```powershell
pwsh -File .\scripts\start.ps1
```

访问 <http://localhost:5188> 即可管理模块。默认自用模式不要求初始化、登录、密码、令牌或 CSRF；系统自动复用已有启用管理员，没有管理员时创建内部自用身份，供模块归属记录使用，不需要用户保存账号。

自用模式下，可访问站点的请求直接拥有管理权限；Cookie 或旧令牌不会阻挡访问。账号、授权和令牌功能在代码中保留，只有明确设置 `Repository:RequireLogin=true` 才启用账号模式；当前默认无需设置。

启动脚本默认使用仓库下 `.local/data` 存储数据；该目录已排除在 Git 之外。切换端口或明确指定数据目录：

```powershell
pwsh -File .\scripts\start.ps1 -Port 5189 -DataDirectory E:\ModuleRepositoryData
```

`-NoBuild` 适用于已完成 Release 构建后启动。运行实例会锁定自己的二进制文件，重新构建前先在其终端按 Ctrl+C 停止。

直接运行 Web 项目时，`Repository:DataDirectory` 默认为 Web 项目中的 `data`。为保证每次读取相同数据，推荐使用启动脚本或明确传入绝对数据目录。

启动脚本默认监听本机回环地址；已发布实例另外监听明确的内网 IP。默认不限制 Host 名称。尚未配置开机启动或 Windows 服务。

## 首次使用

1. 打开页面直接进入模块列表。
2. 通过“分类管理”建立分类，可添加子分类。
3. 自用模式无需创建账号、分配权限或访问令牌。
4. 新建模块，指定稳定 ID，填写功能摘要、语言、环境、标签和文档。
5. 在“草稿文件”上传单个或多个文件，或选择“解包 ZIP”保留相对目录。也可以通过“输入代码”创建或覆盖单个文本文件。
6. 自用模式下直接管理所有模块；账号模式才需要在“访问权限”中授权。
7. 在“发布更新”填写新版本号与本次说明，发布当前草稿。
8. 检索、阅读、预览并下载最新发布，无需登录。

`examples/hello-module` 提供最小样例。可将其内容打成 ZIP，或上传文件。普通文件模式原样保存 ZIP；只有选择“解包一个 ZIP”才展开内容。

## 草稿和发布

- 编辑草稿和上传文件不会修改当前发布。
- 普通上传按相对路径合并；“替换全部草稿文件”会移除草稿中的旧条目。
- 发布前要求分类、文档、源码文件、版本号和更新说明齐全。
- 同一当前版本不能原地重发；每次发布产生递增序号，代码、元数据、文档和清单在同一数据库事务中切换。
- 不提供旧发布下载、版本回退和历史源码备份。新的草稿可能与当前发布同时存在，这是编辑隔离，不是历史版本服务。
- 新发布切换后，旧内容失去引用时清理。异常中断留下的孤立文件在后续操作或下次启动时清理。
- 若草稿被其他编辑操作修改，旧页面保存、上传或发布会收到冲突提示；先保存自己需要的文本，再刷新重试。
- 同一数据目录仅支持一个运行实例。当前数据库为初始 schema，通过 `EnsureCreated` 建立；本次未提供后续 schema 升级迁移程序。

## 权限规则

下面的规则仅在可选账号模式下生效。默认自用模式直接拥有全部模块管理权限，不显示账号、令牌和退出登录入口。

| 身份 | 可读当前发布 | 可编辑／发布 | 可管理授权、停用模块 |
| --- | --- | --- | --- |
| 管理员 | 全部 | 全部 | 全部 |
| 所有者 | 自己的模块 | 自己的模块 | 自己的模块 |
| 维护者 | 获授权模块 | 获授权模块 | 否 |
| 使用者 | 获授权模块 | 否 | 否 |

未发布模块和停用模块仅管理者可见。已下载内容不会因撤销权限而被远程删除。

账号停用会使现有登录及令牌后续请求失败。账号修改使旧浏览器会话失效；重置密码同时撤销该账号的访问令牌。系统拒绝停用或降级最后一个启用的管理员。

访问令牌默认只读、仅创建时显示完整值，服务端只保存摘要。界面不从项目文件读取令牌，也不将令牌写入源码或日志。

账号模式也不限制密码复杂度或最小长度，允许空密码、简单数字和中文账号；首次初始化允许通过内网地址完成；没有登录频次限制。

## 文件限制

- 最多 500 个文件，模块未压缩总大小不超过 32 MiB。
- 路径统一为模块内相对路径；拒绝 `..`、绝对路径、Windows 保留名、`.git`、`.module-repository` 等内容。
- 拒绝 ZIP 链接、特殊文件、重复路径和文件／目录冲突；大小写不同也视为冲突。
- 服务端按内容哈希存储文件，不执行上传源码、HTML 或脚本。
- UTF-8 文本支持最大 256 KiB 预览；其他内容通过下载查看。
- Markdown 界面支持标题、段落、列表和代码块；原始 HTML 作为文本处理，不执行。
- Unity 资源中的 `.meta` 按普通文件保留，不自动生成或修复资源引用。

## 测试

先停止占用 Release 输出的运行实例，再运行：

```powershell
dotnet test ModelRepository.sln -c Release
node --check src/ModuleRepository.Web/wwwroot/app.js
```

集成测试使用独立的临时 SQLite、内容目录和随机测试凭据，不连接用户数据。测试覆盖权限、撤销、发布隔离、过期修订、ZIP 校验及失败时当前内容可用性等。

## 代码入口

- `src/ModuleRepository.Web/Program.cs`：启动、自用／账号模式、数据目录和单实例。
- `src/ModuleRepository.Web/Api.cs`：管理和读取 API。
- `src/ModuleRepository.Web/Services/ModuleService.cs`：草稿、发布、授权、读取与下载。
- `src/ModuleRepository.Web/Services/ContentStore.cs`：内容哈希、文件限制、ZIP 校验和清理。
- `src/ModuleRepository.Web/wwwroot`：无外部 CDN 依赖的响应式管理界面。
- `tests/ModuleRepository.Tests`：HTTP 与真实 SQLite 集成测试。

实现参考：[Microsoft Cookie 认证文档](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0)、[CSRF 文档](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)、[EF Core SQLite 文档](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/)。
