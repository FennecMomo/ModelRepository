# 通用百分比格式化 API

模块 ID：`stagec-common-percent`。命名空间：`AgentReuse`。

```csharp
public static string PercentFormatter.Format(decimal ratio)
```

将比例乘以 100，以 `CultureInfo.InvariantCulture` 固定保留两位小数，然后追加 `%`。仅依赖 BCL；WPF、ASP.NET 和 Unity 的消费工程负责各自的界面或接口适配。

```csharp
PercentFormatter.Format(0.75m);  // "75.00%"
PercentFormatter.Format(0.125m); // "12.50%"
```

上述输出行为自 1.1.0 起生效。1.0.0 的对应输出为 `75%`、`12.5%`。

2026-10-23 在本地测试仓库 `http://127.0.0.1:5194` 发布 1.1.1 / revision 3，仅将 API 文档示例修正为上述固定两位小数形式，未修改算法或文件。通过发布 API 重新读取 manifest，确认文档已更新、源码文件清单和 SHA-256 与 1.1.0 相同。

- repositoryId：`6f37695ef3774d27a8592eb76d74d6f0`
- 1.1.1 contentHash：`783eff699fd4ae7675ce2a9445f4591bfccb78f89b8537f86fc8c88909d5587d`
- PercentFormatter.cs SHA-256：`021538d31f2e8f54b7200c1a8142754948bdb60b02ab0a889167444e72ca0841`，247 字节。

发布元数据改变，因此 contentHash 改变；源码哈希保持不变。三环境实际运行证据仍对应 1.1.0，详见 [阶段 C 验证汇总](../stage-c-agent-integration-results.md)。本次只做文档发布与 manifest 一致性核对，没有重复构建或重跑三环境。

Git 保存本说明和产品代码；测试仓库数据库、原始运行材料及安装包继续留在本地忽略目录，不随 GitHub 推送上传。修正前后 manifest 分别保存在 `.local/stage-c-agent/percent-before-doc-fix.json` 和 `percent-after-doc-fix.json`，历史交接文件保持原样。
