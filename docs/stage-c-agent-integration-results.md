# 阶段 C：真实 Agent Skill / MCP 跨环境接入结果

验证日期：2026-10-23。产品基线：本地提交 `45558ff1490e5d8a92421edd14238b9629ea1972`（0.3.0）。

本轮三个新会话均完成两阶段验证：Agent 读取 module-repository Skill，通过宿主原生 MCP 检索并安装同一份百分比模块，再发现并升级百分比模块和已有加权模块，最后实际构建、启动消费应用。所有指定正常链路通过。本结论来自本轮证据，不以阶段 B 或旧 CLI/协议测试替代。

## 场景与可证明范围

业务任务明确要求优先复用团队百分比实现并检查已有模块；会话被要求使用原生 MCP，但没有被逐条指定 Skill 读取命令或工具执行顺序。三个 Agent 自行选择并读取 `C:/Users/renqi/.codex/skills/module-repository/SKILL.md`，发现宿主工具后完成接入。因此验证了有复用业务意图的自主 Skill 选择与真实宿主 MCP 调用；完全不提示仓库/复用的盲测触发率未测试，也不保证每次开发必然触发。

原生工具包括 module_search、module_get、module_list_installed、module_check_updates、module_plan_apply、module_apply。首次安装使用 moduleId 和目标目录；升级使用实际安装记录中的 installationId。调用结果及参数保存于各环境报告目录，没有用 CLI 或手写 JSON-RPC 替代源码安装和更新。此前 stdio 协议冒烟仅证明协议层，本轮增加了宿主加载、Agent 工具选择和应用消费证据。

共享测试服务为 `http://127.0.0.1:5194`，repositoryId 为 `6f37695ef3774d27a8592eb76d74d6f0`。各联网 MCP 调用显式指定该地址。5188 正式服务不参与测试。主会话等待三份 v1-ready 成功后，统一发布 1.1.0，再续发第二阶段任务；发布标记和续发标记均已保存。

| 环境 | 本轮新会话 | 原消费工程（相对仓库根目录） | 实际运行方式 |
| --- | --- | --- | --- |
| WPF | 01a16787-95c0-7ad3-8dfe-5ea74d398ec9 | .local/integration-projects/wpf/second-round/Consumer | net10.0-windows 真实窗口、输入/命令/结果绑定 |
| ASP.NET | 01a16787-9e90-7900-9bf2-a4a373382d5f | .local/integration-projects/aspnet/second-round/runs/actual/app | .NET 10.0.12 实际 HTTP 服务，5293 |
| Unity | 01a16787-b01b-71e1-a20d-4f01ef2380c6 | .local/integration-projects/unity/second-round/UnityCrossConsumer | UnityCLI / Editor 6000.5.10f1 构建 Windows Mono Player 并运行 |

## 两阶段结果

| 检查项 | WPF | ASP.NET | Unity |
| --- | --- | --- | --- |
| Skill 自主选择、原生 MCP 检索和安装百分比 1.0.0 | 通过 | 通过 | 通过 |
| v1 实际输出 75%、12.5%，原加权 80、25 | 通过 | 通过 | 通过 |
| 原生 MCP 发现两个 1.1.0 更新 | 通过 | 通过 | 通过 |
| 按原安装 ID 计划、覆盖更新两个模块 | 通过 | 通过 | 通过 |
| v2 输出 75.00%、12.50% | 通过 | 通过 | 通过 |
| 加权 [1,2] / [2,1] 返回 decimal 1.33 | 通过 | 通过 | 通过 |
| 原加权 [60,80,100]/[1,2,1] = 80；[0,100]/[3,1] = 25 | 通过 | 通过 | 通过 |
| 统一发布、安装记录和磁盘源码哈希一致 | 通过 | 通过 | 通过 |
| 最终 upToDate、pendingOperations 为空 | 通过 | 通过 | 通过 |
| 项目额外文件、其余三份模块保持 | 通过 | 通过 | 通过 |

两阶段必要构建通过，v2 三环境均记录 0 警告、0 错误。WPF 新版窗口加载/可见、有原生 HWND，5 个指定向量的绑定均 Active，退出码 0。ASP.NET 的健康、百分比、加权、验证接口均实际 HTTP 200。Unity Player 实际执行 5 模块/12 向量，UGUI Text 读回成功，退出码 0；以 batchmode/nographics 运行，不代表视觉效果或人工交互验收。

纯 BCL 的两份 C# 模块源码直接兼容三个环境，没有为不同环境改写算法。工程仍需消费适配：WPF 接命令和文本绑定，ASP.NET 接路由及业务调用，Unity 接 TrainingWorkflow 和 Canvas/UGUI。新版 WPF/Unity 的验证入口调整预期值；ASP.NET 沿用 v1 接入代码。实际数值断言验证 1.33，不能仅凭显示格式舍入声称算法更新成功。

## 发布及源码身份

同一阶段三环境使用相同版本、revision、contentHash 和源码 SHA-256。v1 为 1.0.0 / revision 1；v2 为 1.1.0 / revision 2。

| 模块/阶段 | contentHash | 源文件 SHA-256 |
| --- | --- | --- |
| stagec-common-percent v1 | e42365d605a1bfca2976b68f126e7c779ea15d92e39a5ac5d8f72c190a4c4947 | 26dee7b1b93f5a52580afda860838bb5c601fb5a41ea2574e1c3d6de25f6de30 |
| cross-common-score v1 | 98d2f9c82067cf5fb4c69e4ebaa542616e06bc0d05324f16e48892ffff50283e | 3a5356188414737b86d63cad068ba82289b8b1b26d92980786bed62102bdeb7c |
| stagec-common-percent v2 | 5c92959ceb7c6ee908ec92de8f581944d10297f5f7705ce04d40761eb73a5712 | 021538d31f2e8f54b7200c1a8142754948bdb60b02ab0a889167444e72ca0841 |
| cross-common-score v2 | 6f9761725fdc4a1555c42f0068b246de6ed67e527da5b35a0af30328fe36e5d5 | 35497a135402c2fdc934420738af877a9bacdc167489914372d0b7c38ef4a9c4 |

主会话汇总时重新读取三个工程当前 modules.json，逐项比较统一发布的 repositoryId/version/revision/contentHash，并重新计算六份落地源码哈希，核对文件清单与 pending 状态，全部相符。原始运行 JSON、原生 MCP 返回记录和 Git 差异同时核对；没有只依赖最终 success 字段。

Git 源码差异为百分比格式 `0.##` → `0.00`，加权返回值增加 `Math.Round(sum / total, 2, AwayFromZero)`。源码只由 MCP apply 覆盖。三个工程原有修改、未跟踪内容和阶段一证据均保留；没有 reset/clean。WPF/ASP.NET 两模块目录额外说明文件的升级前后哈希不变；Unity 的源码 .meta、目录 .meta 与 TrainingWorkflow 保留。其余三个模块仍为 1.0.0。

## 已知问题与未验证项

- 验证时百分比 1.1.0 API 文档仍写旧版 75%/12.5%，changeNotes 和实际源码已是两位小数。2026-10-23 后续已按用户要求发布仅修正文档的 1.1.1 / revision 3，示例改为 75.00%/12.50%，源码 SHA-256 不变。上文三环境结论仍对应冻结的 1.1.0；未将修正文档后的新版本冒称为重新进行三环境验证。正确 API 说明见 [百分比模块 API](modules/stagec-common-percent.md)。
- WPF 的 libpng profile 警告、Unity 构建末尾 Mono/debugger 提示未阻断构建/运行。Unity 全工作区 diff --check 有既有或生成 scene/meta 尾空格；限定消费源码/记录范围检查通过，不宣称全工作区零问题。
- ASP.NET 启动方式审核拒绝、重编译文件占用、证据脚本 decimal 解析及 WPF 保留检查脚本布尔分组曾产生中间失败；原始失败和修正证据保留。最终 MCP/运行链路通过，没有以修改模块源码绕过断言。
- 本轮最终 wait_threads 快照查询宿主失败；最终完成判断采用已落盘 phase=v2/status=completed 的交接、原生调用记录、真实运行输出和磁盘复核。之前快照已记录三个新会话的原生更新进度。
- 未测试：完全无复用提示的 Skill 自动触发率、全面边界/区域设置输入、网络中断/冲突恢复、并发性能、安全专项、人工视觉/DPI、多设备和其他平台、生产部署。各工程 PENDING-TESTS.md 保留这些未测范围。既有阶段 C 待测清单中宿主正常接入项由本报告提供新增证据，其他项目不据此视为通过。

## 本地证据索引与交付

证据根目录：`E:/etc/ModelRepository/.local/stage-c-agent`。原始材料在忽略目录，仅汇总文档提交 Git，不将构建产物或运行私有资料加入仓库。

| 环境 | 报告 | 原生 MCP | v2 原始运行 |
| --- | --- | --- | --- |
| WPF | wpf/TEST-REPORT.md | wpf/mcp/all-calls.json；wpf/mcp/v2-all-calls.json | wpf/evidence/v2-runtime.json |
| ASP.NET | aspnet/TEST-REPORT.md | aspnet/v2-initial-native-checks.json；v2-percent-plan/apply.json；v2-score-plan/apply.json；v2-final-native-checks.json | aspnet/v2-http-stage-c.json；v2-http-summary.json |
| Unity | unity/TEST-REPORT.md | unity/run-20261023-145310/native-mcp-transcript.json；unity/v2-20261023-150402/v2-native-mcp-transcript.json | unity/v2-20261023-150402/v2-player-runtime.json |

交接：handoff/{wpf,aspnet,unity}-v1-ready.json、*-result.json、v2-published.json、两份 *-v2-manifest.json。工程初始、升级前后 Git 差异及文件备份分别在环境报告的 backup/before/after 或 evidence 目录，准确文件名见各 TEST-REPORT.md。

三端消费应用均已退出。共享 5194 服务在主会话核对 EXE 路径后统一停止，5188 未操作。仅创建本报告的本地提交，不推送。
