# 阶段 A API

服务端根地址默认为 `http://localhost:5188`。API 均在 `/api` 下，响应包含私有数据时禁止缓存。

## 认证

默认自用模式：接口无需登录、Cookie、令牌或 `X-CSRF-Token`，直接以自用管理员身份操作。`GET /api/session` 返回 `personalMode=true`、`needsSetup=false`、当前管理身份、应用版本，`csrfToken=null`。

可选账号模式：只有设置 `Repository:RequireLogin=true` 才要求登录及写请求的 CSRF。此时管理界面使用 Cookie，CSRF token 由 `/session` 返回；只读访问令牌按原有账号和模块授权读取数据。

账号模式允许空密码、简单数字和 1–80 字符中文账号，取消登录限流和 localhost 初始化限制。

## 端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/session` | 初始化状态、当前账号、CSRF token |
| POST | `/setup` | 账号模式下首次创建管理员；字段 username/password/displayName，允许内网初始化 |
| POST | `/login` | 账号密码登录 |
| POST | `/logout` | 退出 |
| GET/POST | `/users` | 管理员列出或创建账号 |
| PUT | `/users/{id}` | 管理员更新 displayName/enabled/isAdmin/newPassword |
| GET/POST | `/tokens` | 当前用户列出或创建只读令牌；创建字段 name |
| DELETE | `/tokens/{id}` | 撤销自己的令牌 |
| GET/POST | `/categories` | 读取分类、管理员创建 name/parentId |
| PUT/DELETE | `/categories/{id}` | 管理员修改或删除分类；拒绝循环和删除被引用分类 |
| GET | `/modules?q=&categoryId=&language=&environment=&tag=` | 授权范围内检索；分类含子分类 |
| POST | `/modules` | 创建 id/metadata；当前用户成为所有者 |
| GET | `/modules/{id}` | 当前发布；有维护权限时同时返回草稿及修订 |
| PUT | `/modules/{id}/draft` | metadata/expectedDraftRevision |
| POST | `/modules/{id}/files` | multipart：files、format=files/zip、mode=merge/replace、expectedDraftRevision |
| POST | `/modules/{id}/snippet` | path/content/expectedDraftRevision，新增或覆盖 UTF-8 文件 |
| DELETE | `/modules/{id}/files` | JSON body：path/expectedDraftRevision，移除草稿文件 |
| GET | `/modules/{id}/preview?kind=draft或published&path=` | 安全文本预览；草稿需维护权限 |
| POST | `/modules/{id}/publish` | version/changeNotes/expectedDraftRevision |
| GET | `/modules/{id}/download?revision=` | 下载指定当前发布的 ZIP；旧序号返回 409 |
| GET | `/modules/{id}/grants` | 所有者或管理员读取授权账号 |
| PUT | `/modules/{id}/grants` | username/role；role 为 reader、maintainer、none |
| PUT | `/modules/{id}/enabled` | 所有者或管理员设置 enabled |

`GET /health` 返回运行状态和应用版本，不包含模块或账号信息。

## metadata

```json
{
  "name": "问候示例",
  "summary": "生成简单问候文本",
  "language": "C#",
  "environment": ".NET / Windows；其他环境未验证",
  "categoryId": null,
  "tags": ["示例"],
  "documentation": "接口与最小使用示例，支持 Markdown",
  "dependencies": "无第三方依赖",
  "limitations": "仅用于演示",
  "validation": "未验证",
  "source": "",
  "license": ""
}
```

草稿可暂时不设置分类和文档，但发布前必须完善。接口版本号采用 `1.0.0` 或 `1.0.0-beta` 形式，真正的发布身份同时包含递增 revision 和 contentHash。

## 错误约定

- 400：输入、路径、上传、发布条件或 CSRF 校验失败。
- 401：未登录、凭据失效或账号停用。
- 403：已知获授权模块上的操作权限不足，或使用只读令牌写入。
- 404：模块不存在或不可访问，对这两种情况返回一致信息。
- 409：ID 不可用、草稿修订过期、当前发布变化、分类仍被使用等冲突。
- 500：存储或内部失败，响应不返回内部路径、调用栈或凭据。

多数错误返回 `{ "error": "说明" }`。框架认证和请求绑定错误可能只返回状态码，客户端应提供对应提示。
