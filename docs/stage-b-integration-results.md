# 阶段 B 多环境接入验证汇总

日期：2026-10-23。三个独立测试会话均已完成；主会话读取了各报告，并现场核对独立工程 Git HEAD、更新差异、安装记录及 v2 运行结果 JSON。未重复执行测试。

## 结论

0.2.0 模块仓库的 CLI 安装与覆盖更新链路在本机 WPF、ASP.NET Core、Unity 三种实际工程中通过。各环境使用适配自身的模块；这不表示任意同一份源码可以不加修改地跨三种框架使用，也不表示阶段 C 的 MCP/Skill 已完成。

| 环境 | 实际证据 | 更新后行为 |
| --- | --- | --- |
| WPF / .NET 10 | v1/v2 Release 编译和真实窗口进程通过；每版 12 条程序化窗口、绑定、输入及命令断言 | 输入 85 时由“正常”变为“接近上限” |
| ASP.NET Core 10 | v1/v2 编译、启动及实际 HTTP 请求通过 | 相同报价请求由总价 140 变为 108；额外项目适配端点正常返回 |
| Unity 6000.5.10f1 | UnityCLI 1.0.0-beta.6 创建真实工程；两版 Editor 验证、Windows Player 构建与批处理运行通过 | 训练完成 2/3 步，加权进度 75%，实际输出新版摘要；源码和 .meta 更新、既有 GUID 保留 |

三个工程均执行 search、download、plan、install、list、check-updates、update。均验证受管本地修改覆盖、旧文件删除、新文件加入及项目额外文件保留。最终安装版本均为 1.1.0，pendingOperations 为空，测试时更新检查为 upToDate。

## 工程与本地证据

以下路径相对主仓库，位于忽略目录 .local，未上传 GitHub。每个工程有独立 Git，基线和 v1 安装已提交，v2 更新保留为工作区差异。

| 环境 | 工程 | v1 HEAD | 报告 |
| --- | --- | --- | --- |
| WPF | `.local/integration-projects/wpf/run/Consumer` | `89b0271` | `.local/integration-projects/wpf/TEST-REPORT.md` |
| ASP.NET | `.local/integration-projects/aspnet/runs/verified/app` | `45ff39a` | `.local/integration-projects/aspnet/TEST-REPORT.md` |
| Unity | `.local/integration-projects/unity/UnityModuleConsumer` | `0ffa2e8` | `.local/integration-projects/unity/TEST-REPORT.md` |

各报告附复现脚本、原始输出、退出码、安装清单及 Git 差异位置。ASP.NET 新增文件的完整差异应结合报告保存的证据和未跟踪文件查看，普通 git diff --stat 不包含所有未跟踪文件。

## 发现与边界

ASP.NET 和 Unity 初轮遇到测试脚本 Git 调用、URL 尾斜线比较、Unity 模板 ID 或参数拼接问题，修正后完成验证；历史失败保留，不能声称首次全流程一次通过。未为通过测试修改模块仓库产品代码。

发布产物的文件/API 版本为 0.2.0，但构建发生在提交之前，ProductVersion 内的提交后缀仍指向先前 19371b3。判断本次测试产物应结合报告中的文件哈希，不能把该后缀当作最终源码提交号。

Unity 退出日志存在 abort_threads/debugger-agent 提示；构建结果、进程退出码和实际业务 JSON 通过，不宣称零警告。测试服务均由各会话报告已停止，测试未连接或重启真实 5188 服务。

人工界面效果、完整人工交互、不同屏幕/DPI、其他设备/平台、故障恢复、并发、压力、安全专项和全面回归均未测试。各工程 PENDING-TESTS.md 保留具体清单。本次仅证明已选模块在本机对应开发环境中的正常接入与更新路径可用。
