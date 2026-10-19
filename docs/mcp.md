# FUMO MCP 接入说明（第一阶段）

日期：2026-10-18。适用于默认后端 FUMO。

## 设计原则

- **一个网关工具**：默认只向模型声明 `mcp`，参数 Schema 按需获取，避免工具倾倒。
- **懒启动**：模型第一次调用 `mcp` 才启动/连接服务器；空闲 5 分钟回收。
- **显式信任**：项目级配置默认不加载，需用户配置 `trustProjectConfig: true`。
- **纯托管**：客户端是 C#，不依赖 Node/Python。

## 配置文件

用户级：`~/.fumo/mcp.json`  
项目级：`<项目>/.fumo/mcp.json`（仅在用户配置信任后加载）

```json
{
  "trustProjectConfig": false,
  "servers": {
    "github": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": { "GITHUB_TOKEN": "${GITHUB_TOKEN}" },
      "timeoutSeconds": 60
    },
    "remote": {
      "transport": "http",
      "url": "https://example.com/mcp",
      "headers": { "X-Client": "fufucode" },
      "auth": {
        "type": "oauth",
        "grant": "authorization_code",
        "authorizationUrl": "https://example.com/oauth/authorize",
        "tokenUrl": "https://example.com/oauth/token",
        "clientId": "fufucode",
        "scopes": ["mcp.read"]
      }
    },
    "static-token": {
      "transport": "http",
      "url": "https://example.com/mcp",
      "auth": { "type": "bearer", "tokenEnv": "EXAMPLE_MCP_TOKEN" }
    }
  }
}
```

### 认证方式

| 类型 | 字段 | 行为 |
|---|---|---|
| `bearer` | `tokenEnv` 或 `token` | 直接发送 `Authorization: Bearer` |
| `oauth` + `client_credentials` | `tokenUrl` / `clientId` / `clientSecret` / `scopes` | 自动换取令牌；401 时重试一次 |
| `oauth` + `authorization_code` | 额外 `authorizationUrl`、可选 `redirectPort` | 打开浏览器，本地回环接收回调，PKCE + state 校验，令牌缓存并自动刷新 |

令牌缓存在 `~/.fumo/mcp-tokens/<服务器名>.json`（明文 JSON，仅当前用户目录）。

## 模型侧用法

系统提示只列服务器名。模型通过一个工具操作：

```json
{ "action": "list_servers" }
{ "action": "list_tools", "server": "github" }
{ "action": "call", "server": "github", "tool": "create_issue",
  "arguments": { "owner": "me", "repo": "demo", "title": "..." } }
```

- `list_tools` 返回工具名、说明与 `inputSchema`；
- `call` 结果带 `[MCP server/tool]` 来源标记，按不可信数据处理；
- 单次输出上限 64KB，超出截断。

## 生命周期与错误

- stdio 子进程崩溃后，下一次调用自动重建；
- 请求超时（默认 60 秒，可配置）会返回结构化错误；
- 服务器 stderr 保留尾部用于诊断；
- 会话关闭时停止全部 MCP 子进程。

## 已知边界（第一阶段）

- 不支持 MCP prompts、resources、UI（MCP Apps）；
- 不支持 tools/list_changed 推送（重新 `list_tools` 可看到最新结果）；
- OAuth 仅支持显式配置的授权/令牌端点，不做动态客户端注册与元数据发现；
- 项目级配置不做交互式确认，只认 `trustProjectConfig` 开关。
