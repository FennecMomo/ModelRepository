# 阶段 B：本地接入（0.2.0）

阶段 B 提供共享协议、.NET 客户端库和 CLI，支持下载当前发布、安装到指定目录、覆盖更新和项目安装记录。服务端不操作客户端电脑的目录。MCP 和复用 Skill 留在阶段 C，前端由独立会话负责。

## 使用前准备

服务端和 CLI 均使用 .NET 10。服务端需要 0.2.0 或以上的阶段 B 接口。源码目录执行：

```powershell
dotnet build ModelRepository.sln -c Release
pwsh -File .\scripts\module-repo.ps1 --help
```

以下用源码包装脚本演示。发布后的 `module-repo.exe` 接收同样的参数。`--project` 默认为当前工作目录，必须是已存在的项目；`--target` 相对该项目解析，也接受项目内的绝对目录，记录时转换成相对路径。模块推荐安装到独立子目录，同一模块可以安装多个副本。

默认自用模式直接连接，无需登录、令牌或临时授权码。只有服务端主动启用账号模式时，CLI 才需要通过进程环境变量 `MODULEREPO_TOKEN` 读取已有只读令牌。令牌不会写入安装记录。可以通过 `MODULEREPO_SERVER` 设置默认服务地址。

## 常用命令

```powershell
# 检索模块，取得模块 ID
pwsh -File .\scripts\module-repo.ps1 search --server http://localhost:5188 --query 日志 --language C#

# 只下载当前发布，生成 ZIP 和旁边的 .zip.manifest.json
pwsh -File .\scripts\module-repo.ps1 download --server http://localhost:5188 --module my-module --output E:\Downloads\my-module.zip

# 生成安装计划，不写入项目文件或安装记录
pwsh -File .\scripts\module-repo.ps1 plan --server http://localhost:5188 --module my-module --project E:\MyProject --target Assets/Reusable/MyModule

# 安装当前发布；--dry-run 可以只看计划
pwsh -File .\scripts\module-repo.ps1 install --server http://localhost:5188 --module my-module --project E:\MyProject --target Assets/Reusable/MyModule

# 离线查询项目安装记录
pwsh -File .\scripts\module-repo.ps1 list --project E:\MyProject

# 根据项目记录主动检查所有已安装副本，不写入源码
pwsh -File .\scripts\module-repo.ps1 check-updates --project E:\MyProject

# 使用 list 中的 installationId 更新对应副本
pwsh -File .\scripts\module-repo.ps1 update --project E:\MyProject --installation 安装ID

# 只有一个对应副本时，也可以按模块 ID 选择
pwsh -File .\scripts\module-repo.ps1 update --project E:\MyProject --module my-module
```

更新默认使用记录中的服务器地址。服务迁移后可使用 `--server` 覆盖地址；仓库持久身份必须相同，不能把同名模块的另一仓库当作原来源。`--module` 有多个副本时可加 `--target` 选择，或直接传安装 ID。

CLI 除帮助外输出 JSON，错误输出到 stderr。供脚本或 Agent 使用时推荐直接运行构建/发布后的 `module-repo.exe`，避免首次构建输出混入 JSON。退出码为：0 成功，1 操作错误，2 参数错误，3 冲突或计划已变化，4 写入未完成，130 用户取消。`check-updates` 成功查询本地记录后，会逐副本返回状态；调用方需要检查每个 `updates[].status`，不能仅凭进程退出码判断全部为最新。

## 覆盖规则

| 目标文件情况 | 行为 |
| --- | --- |
| 新版新增，目标不存在 | 新建 |
| 已受管，当前内容不同于新版 | 覆盖，包括项目内自行修改过的内容 |
| 已受管，内容与新版一致 | 保留内容，记为 unchanged |
| 旧版受管、新版已移除 | 删除该文件，包括本地修改过的旧文件 |
| 项目额外文件，新版没有占用其路径 | 保留 |
| 新版路径已被非受管文件占用 | 返回冲突，不开始安装 |
| 与另一个模块的安装目录相同或互为父子 | 返回冲突，使用独立子目录 |
| 文件与目录互相转换、受管路径含链接 | 不自动处理，先整理项目路径再重新计划 |

删除文件后不递归清空目录。第一次安装不会凭文件内容相同就接管已有非受管文件。对同一版本再次执行 update 仍会按发布内容恢复受管文件，因此它也会覆盖本地修改；`check-updates` 的 upToDate 只表示发布标识一致，不代表本地文件没有修改。

工具只替换模块文件，不运行模块安装脚本，不自动安装依赖，不修改调用代码，不自动执行 Git 提交。更新前后由用户和 Agent 使用项目 Git 查看差异并处理影响；工具不强制项目必须使用 Git，也不生成旧源码备份。

## 安装记录与失败状态

项目内 `.module-repository/modules.json` 是唯一持久安装清单，建议随项目 Git 提交：

- `schemaVersion`：记录格式版本，当前 1。
- `installations`：成功安装副本，包含安装 ID、仓库身份、来源地址、模块 ID、相对目标目录、版本、发布序号、内容哈希、安装时间、受管文件路径/哈希/大小。
- `pendingOperations`：写入前登记的意图，包含目标发布、状态及已处理文件；完成后移除。

没有旧版源码、令牌或本机项目绝对路径。来源服务器地址属于项目元数据，勿把凭据放入 URL。操作期间的排他锁 `operation.lock` 自动释放，无需手动领取授权。

安装先在内存中下载并核对整个当前包，再逐文件写入。单文件通过临时新内容替换，临时文件不是旧版备份。多个文件的安装不具备整体事务回滚：写入中途失败时，可能已有部分文件更新。命令返回 incomplete、已处理和剩余列表，记录保留 pending；已有成功版本不会因此改记为成功更新。排除故障后对同一安装重新执行 update/plan，工具依据记录和文件实际哈希重新计划。硬中断可能来不及写入最新处理进度，此时不能只凭进度列表判断磁盘内容，应结合文件和 Git 检查。

计划执行前重新核对当前发布、安装记录和相关文件状态，变化则重新计划。CLI 每次命令独立生成计划；plan 的 JSON 用于查看，不能从磁盘直接回传执行。库调用可在同一进程保留 `InstallPlan` 后调用 `ApplyAsync`。

## 服务端与客户端接口

新增接口沿用现有自用/可选账号访问方式：

| 接口 | 结果 |
| --- | --- |
| `GET /api/repository` | `repositoryId`、服务 `version`、`schemaVersion` |
| `GET /api/modules/{id}/manifest` | 当前发布的仓库身份、模块 ID、版本、发布序号、发布时间、更新说明、元数据、内容哈希、文件路径/哈希/大小 |
| `GET /api/modules/{id}/download?revision=N` | 既有下载接口；只能下载当前发布，序号过期返回 409 |

清单仅返回已发布且启用的内容，不混入草稿。`repositoryId` 持久保存在服务数据目录的 `repository-id.json`，迁移或备份服务时随当前业务数据保留。删除此文件会改变仓库身份，现有项目将拒绝把该来源当作原仓库更新。

`ModuleRepository.Contracts` 定义共享格式；`ModuleRepository.Client` 的 `RepositoryClient` 负责检索、清单、下载及核对，`ModuleInstaller` 负责 `ListAsync`、`PlanAsync`、`InstallAsync`、`UpdateAsync`、`ApplyAsync`、`CheckUpdatesAsync`。CLI 直接复用这些逻辑，后续 MCP 可以复用同一库。

## 本次必要验证

2026-10-23：Release 解决方案构建通过，0 警告、0 错误。独立数据和临时项目的一条正常链路通过：创建多文件模块 → 发布 1.0.0 → 下载及清单 → 计划无落盘 → 安装 → 修改本地受管文件并添加项目额外文件 → 发布 1.1.0 → 发现更新 → 覆盖/新增/删除且保留额外文件 → 安装记录与最新状态一致。

脚本：`scripts/smoke-stage-b.ps1 -Server http://127.0.0.1:5190`，只对专用测试实例运行，会创建测试分类和模块。脚本不启动或停止服务，不接触真实项目，测试产物留在忽略目录 artifacts 下。未运行全面回归、安全性、故障注入、界面效果测试；后续范围见 [后端待测试清单](backend-pending-tests.md)。
