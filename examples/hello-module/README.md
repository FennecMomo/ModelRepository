# 问候示例模块

这是用于验证模块仓库上传、发布和下载的最小样例。

## 接口

`Reusable.Examples.Greeting.Hello(string name)` 返回包含名称的问候文本。

## 使用示例

```csharp
var message = Reusable.Examples.Greeting.Hello("开发者");
```

## 适用条件

C# 项目。没有第三方依赖；目录中保留 `src/Greeting.cs` 与本文档即可。

## 验证范围

样例用于仓库管理流程验证，不代表其他平台或真实产品已完成验收。
