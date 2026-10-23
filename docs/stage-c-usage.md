# 阶段 C：MCP 与 Skill 接入（0.3.0）

本地 stdio MCP 与 CLI 共用 ModuleRepository.Client。仓库服务提供内容，本地 MCP 在 Agent 所在电脑安装文件。服务器无需接收客户端目录；阶段 B 的 0.2.0 服务接口也兼容此 MCP。

## 本机安装

需要 Windows、.NET 10 SDK、Codex CLI；运行产物需要 .NET 10 Runtime。源码仓库执行：

```powershell
pwsh -File .\scripts\Install-AgentIntegration.ps1 -Server http://localhost:5188
```

安装脚本发布 MCP/CLI 到 `artifacts/agent-0.3.0/{Mcp,Cli}`，将仓库内 `skills/module-repository` 安装到当前 CODEX_HOME 的 skills（未设置时为用户 .codex/skills），通过 `codex mcp add module-repository` 注册 stdio 服务。仅写入该名称的 MCP 配置和该 Skill 自有文件。相同名称已有自定义配置时，先检查该配置；脚本用于安装/更新本工具。

`-SkillRoot` 可指定 Skill 根目录，`-SkipMcpRegistration` 仅发布并安装 Skill，不注册 Codex。默认自用无需账号，账号模式可通过进程环境 MODULEREPO_TOKEN 提供已有令牌；安装脚本不收集或持久保存令牌。

注册后新建/重新加载会话，使客户端加载工具和 Skill。当前已打开会话未必动态发现新增工具；可暂用已安装 Skill 的 CLI 包装脚本。用 `codex mcp get module-repository --json` 检查注册结果，它不等于工具连接或安装链路已验证。

其他支持 stdio 的 Agent 可配置如下进程（路径换为实际发布路径），无需改服务器：

```json
{
  "mcpServers": {
    "module-repository": {
      "command": "E:\\etc\\ModelRepository\\artifacts\\agent-0.3.0\\Mcp\\module-repo-mcp.exe",
      "env": { "MODULEREPO_SERVER": "http://localhost:5188" }
    }
  }
}
```

## 六个工具

| 工具 | 参数及作用 |
| --- | --- |
| module_search | query、language、environment、tag、server 均可选；检索候选，仍需读取发布详情 |
| module_get | moduleId 必填，server 可选；返回当前 manifest，含功能/API说明、依赖、限制及文件哈希 |
| module_list_installed | project 必填且为绝对目录；离线返回成功安装和 pending |
| module_check_updates | project 必填，server 可选；默认按各安装来源检查全部副本 |
| module_plan_apply | project 必填；首次安装传 moduleId、target；更新传 installationId；server 可选 |
| module_apply | planId 必填；执行同一 MCP 进程产生的计划 |

工具返回 MCP text content 中的 JSON；操作异常设 isError=true 并保留业务错误码，写入未完成返回阶段 B 的部分完成结果。计划存在路径冲突时返回 success=false、status=conflict、planId=null 和清单。更新检查顶层 success=true 仅表示完成查询，必须查看每项 status。

示例调用参数：

```json
{"name":"module_plan_apply","arguments":{"project":"E:\\MyProject","moduleId":"common-score","target":"Reusable/common-score"}}
{"name":"module_apply","arguments":{"planId":"上一步返回的planId"}}
{"name":"module_check_updates","arguments":{"project":"E:\\MyProject"}}
{"name":"module_plan_apply","arguments":{"project":"E:\\MyProject","installationId":"安装记录中的ID"}}
```

计划保存已核对的包与文件状态，不落盘旧源码。内存保留最近16份，执行后移除；MCP退出后失效，没有倒计时或人工授权码。发布或本地状态变化时仍由客户端库拒绝旧计划并要求重新生成。不是可跨进程导入执行的JSON计划。

## 自动使用的含义

Skill 默认允许自动选择：开发可复用功能时，指引 Agent 先检索，再读文档和兼容性说明；进入已有模块项目时主动检查更新。找到合适模块后，在用户当前任务范围内安装并添加工程接入代码；更新前查看受管文件的Git差异，更新后做必要编译/运行检查。与当前任务无关的模块只报告更新，避免无意批量升级。直接使用 `$module-repository` 可以明确触发该流程。

自动选择由宿主Agent决定，Skill不保证每条任意对话都触发。本工具不提供后台定时更新、不自动执行模块脚本、不自动提交Git；用户项目无需为了安装而先建立Git。版本历史与恢复仍交给用户项目Git。

示例开发请求：“使用模块仓库帮这个WPF项目实现负载计算，优先复用现有模块，接入后编译验证。”更新请求：“检查当前项目安装的模块，把训练进度模块更新到当前发布，保留项目自己的适配代码。”

## 实现和必要验证

采用官方 [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md)，固定 NuGet ModelContextProtocol 2.3.0；日志输出stderr，stdout仅承载MCP。Codex注册方式核对了[官方MCP说明](https://developers.openai.com/codex/mcp/)与本机CLI帮助。

2026-10-23：Release解决方案构建通过；Skill校验通过。`scripts/smoke-stage-c.py` 启动独立5195测试仓库及真实MCP进程，完成 initialize、tools/list、全部六工具调用、项目安装、实际C#消费编译运行、发布新版、更新检查及覆盖更新；消费输出42→84，项目额外文件保留，最终版本1.1.0、无pending、upToDate。项目有独立Git，更新diff及完整JSON-RPC证据保存在artifacts/stage-c-smoke-*。测试脚本结束后停止自启进程，不使用真实业务数据。

首次冒烟暴露SDK按请求创建工具实例导致计划丢失；已改为显式绑定同一个工具实例，修复后完整链路通过。没有扩大到全面测试；新会话自动选择Skill、真实宿主重连和其他Agent兼容性仍见待测试清单。

复现前分别将 Web 发布到 artifacts/server-0.3.0、Mcp 发布到 artifacts/mcp-smoke-0.3.0，再执行 `python -X utf8 scripts/smoke-stage-c.py`。脚本依赖Python标准库、dotnet、git；Windows中文环境用 -X utf8。
