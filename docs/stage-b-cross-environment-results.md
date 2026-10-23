# 同源码跨环境与工程交叉取用：第二轮结果

日期：2026-10-23。第二轮三个会话均完成。主会话读取原始运行 JSON，重新计算三个工程全部受管源码 SHA-256，并与当前共享仓库 manifest、安装记录逐一核对。12 个模块/环境组合全部通过，共 27 次共同输入验证。本轮没有沿用第一轮各环境专用模块的成功结论。

## 实际矩阵

四个模块都固定为 1.0.0 / revision 1，来自同一个仓库身份 `6f37695ef3774d27a8592eb76d74d6f0`。

| 模块及来源 | WPF 窗口 | ASP.NET HTTP | Unity Player | 共同结果 |
| --- | --- | --- | --- | --- |
| cross-common-score：统一发布的加权计算 | 2/2 | 2/2 | 2/2 | 80、25 |
| cross-wpf-range：WPF 页面范围逻辑 | 3/3 | 3/3 | 3/3 | 0、85、100 |
| cross-aspnet-price：ASP.NET 报价业务 | 2/2 | 2/2 | 2/2 | 108、70 |
| cross-unity-progress：Unity 训练进度逻辑 | 2/2 | 2/2 | 2/2 | 0.75、0.5 |

各来源模块均被另外两个环境实际调用，构成六条跨工程取用路径；另有三个来源环境自身验证和公共模块三环境验证。三个工程各有四条成功安装记录，pendingOperations 为空。没有消费者重写受管模块源码。

## 同源证据

| 文件 | 三个工程实际 SHA-256（均等于发布清单及安装记录） |
| --- | --- |
| CommonScore.cs | 3a5356188414737b86d63cad068ba82289b8b1b26d92980786bed62102bdeb7c |
| RangePolicy.cs | e9add10face60d334cc28905283dbad4e610660dc10c169e81bab9c4e5bf7d1f |
| PricePolicy.cs | e2454e90b830d3f48c98d7f2a996efa2f59c25b50415b2b942f31fe76610b03b |
| ProgressPolicy.cs | bf2e1b1664f450aebdf59fd63c565907d7eafdd955f85fd85ab3baeba3d0e49f |

公共模块 contentHash 为 `98d2f9c82067cf5fb4c69e4ebaa542616e06bc0d05324f16e48892ffff50283e`。另外三个模块发布身份及全部12格的主会话复核结果保存在 `.local/cross-environment/handoff/root-verification.json`。

WPF 使用真实窗口输入绑定、命令和结果控件验证；ASP.NET 使用实际 HTTP 响应；Unity 使用 UnityCLI 创建的 Unity 6000.5.10f1 工程，Editor 验证和 Windows x64 Mono Player 构建、批处理运行通过。三个环境运行结果 success=true，业务输出均符合共同向量。WPF/ASP.NET 编译及 Unity BuildReport 均记录零错误、零警告；Unity 退出日志另有 debugger-agent/abort_threads 提示，未作专项诊断，不等于全部日志无提示。

## 兼容和提取边界

发布后的四份源码可直接在本轮三个环境编译使用。适配发生在工程层：WPF 处理输入、绑定和命令，ASP.NET 处理HTTP参数和JSON，Unity处理训练状态、MonoBehaviour、Canvas及本地生成的.meta。原版受管.cs没有为消费者修改。

发布前的提取并非证明整套框架代码天然通用：

- WPF 从已经在页面验证的方法中提取范围计算，控件、命令和绑定保留在工程中。
- ASP.NET 先按本轮契约在来源工程实现并通过HTTP验证，再提取报价纯逻辑。相较第一轮，数量改decimal、低额运费改20、九折直接计算；本轮证明冻结后的新契约跨环境一致，不证明旧版所有行为不变。
- Unity 将原float/Mathf实现提取为decimal/BCL。空集合由原来的返回0改为拒绝空数组；其边界已记录。这是有意抽离框架依赖后的复用，不是原UnityEngine代码在WPF中直接运行。

因此，本轮支持“把工程中的通用业务逻辑提取成独立模块，一次发布后由三个环境原样消费”。不支持由此推断任意WPF控件、ASP.NET中间件或Unity组件都可以跨框架直接使用，也未验证其他语言的互用。

## 工程、提交和报告

所有以下路径相对仓库根目录，测试工程和证据位于忽略目录，仅本机保留。每个工程独立Git保留基线、模块安装提交，工程适配作为未提交差异可供检查。

| 环境 | 工程 | 安装提交 | 报告 |
| --- | --- | --- | --- |
| WPF | `.local/integration-projects/wpf/second-round/Consumer` | b136731 | `.local/integration-projects/wpf/second-round/TEST-REPORT.md` |
| ASP.NET | `.local/integration-projects/aspnet/second-round/runs/actual/app` | 5e71046 | `.local/integration-projects/aspnet/second-round/TEST-REPORT.md` |
| Unity | `.local/integration-projects/unity/second-round/UnityCrossConsumer` | e4c10d2 | `.local/integration-projects/unity/second-round/TEST-REPORT.md` |

原始运行结果路径由 `.local/cross-environment/handoff/{wpf,aspnet,unity}-result.json` 的 runtimeEvidence 指定；报告包含复现脚本、来源提取证据、编译与运行退出码、安装记录、Git适配差异和未测试清单。

## 未测试项

人工视觉、完整人工交互、其他屏幕/DPI、Android/WebGL、其他Unity/.NET版本、固定向量之外的金额精度及异常输入、并发/压力、安全、故障恢复和混合版本升级均未测试。各项目PENDING-TESTS.md保存具体范围。本轮没有发现冻结源码在所测环境中的编译或运行不兼容；不等于全面兼容性认证，也不等于MCP/Skill阶段交付。
