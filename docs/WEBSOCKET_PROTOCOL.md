# Sharpclaw WebSocket 协议规范

> 版本: 2.0

## 概述

Sharpclaw Web 宿主通过 WebSocket (`/ws` 端点) 与各客户端 (CLI / TUI / Web UI / QQBot) 通信。
所有消息为 JSON 格式，字段名统一 camelCase。

---

## 客户端 → 服务端

| type | 字段 | 说明 |
|---|---|---|
| `input` | `text: string` | 用户聊天输入（含斜杠命令） |
| `cancel` | — | 取消当前 AI 运行 |
| `command` | `name: string, args?: object` | 执行指令 |

### 内置 command

| name | args | 说明 |
|---|---|---|
| `help` | — | 获取可用指令列表 |
| `config` | — | 查看配置摘要（文本） |
| `getConfig` | — | 获取完整配置 JSON |
| `setConfig` | `{config: object}` | 整体保存配置 |
| `configSet` | `{path: string, value: any}` | 按路径修改单个配置字段 |
| `exit` | — | 断开连接 |

> **斜杠命令兼容**：用户输入以 `/` 开头时，服务端自动解析为 command。
> 例如 `/config set agents.recaller.enabled false` 等同于
> `{"type":"command","name":"configSet","args":{"path":"agents.recaller.enabled","value":false}}`

---

## 服务端 → 客户端

### 对话流

| type | 字段 | 说明 |
|---|---|---|
| `echo` | `text: string` | 回显用户输入 |
| `aiStart` | — | AI 开始回复 |
| `aiChunk` | `text: string` | AI 流式文本片段 |
| `aiEnd` | — | AI 回复结束 |

### 状态

| type | 字段 | 说明 |
|---|---|---|
| `running` | — | AI 正在运行（工具调用等） |
| `inputReady` | — | 等待用户输入 |

### 日志

| type | 字段 | 说明 |
|---|---|---|
| `log` | `text: string` | 日志消息 |
| `status` | `text: string` | 状态描述（如「调用工具…」） |

### 指令响应

| type | 字段 | 说明 |
|---|---|---|
| `commandResult` | `name: string, data?: any, text?: string, error?: string` | 指令执行结果 |

### 错误

| type | 字段 | 说明 |
|---|---|---|
| `error` | `text: string` | 通用错误 |

---

## commandResult 示例

```json
// help 结果
{"type":"commandResult","name":"help","text":"可用指令:\n  /help ..."}

// getConfig 结果
{"type":"commandResult","name":"getConfig","data":{"version":8,"default":{...}}}

// setConfig 成功
{"type":"commandResult","name":"setConfig","data":{"success":true}}

// configSet 成功
{"type":"commandResult","name":"configSet","text":"已设置 channels.web.port = 8080（重启后生效）"}

// 错误
{"type":"commandResult","name":"configSet","error":"属性不存在: foo.bar"}
```

---

## 典型消息流

```
Client                          Server
  │                               │
  │─── input "你好" ─────────────→│
  │                               │
  │←──── echo "> 你好\n" ─────────│
  │←──── running ─────────────────│
  │←──── status "调用工具..." ────│
  │←──── aiStart ─────────────────│
  │←──── aiChunk "你好！" ────────│
  │←──── aiChunk "有什么..." ─────│
  │←──── aiEnd ───────────────────│
  │←──── inputReady ──────────────│
  │                               │
  │─── command getConfig ────────→│
  │←── commandResult {data} ──────│
  │                               │
  │─── command setConfig ────────→│
  │←── commandResult {success} ───│
```
