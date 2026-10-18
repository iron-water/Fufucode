# M0 手机远控原型（N1 阶段一）

打开 `index.html`（file:// 直接可用，无构建、无联网）。**全部为夹具**：数据在 `js/fixtures.js`，界面上 `data-fixture` 元素与底部黄色"原型夹具"模拟条标识非真实行为。不连接任何真实宿主，不代表产品已实现。

## 覆盖界面

- **连接/配对**：6 位配对码（夹具 `426810`）、格式错误与过期提示、已配对/已撤销设备列表。
- **列表**：项目列表 + 会话列表（运行/空闲/上次中断状态点、待审批角标）。
- **聊天**：用户/助手/系统气泡、流式输出游标、工具卡片（完成/失败/待审批）、长消息折行。
- **发送/停止**：发送后按钮切换为停止；停止按 `clear_queue + abort` 语义提示，队列不自动重发。
- **审批**：底部审批页（允许一次/本会话总是允许/拒绝），审批绑定连接代次——断线重连后旧审批显示"已失效"，不继续有效。
- **附件**：＋动作页（照片/手机文件/资料库引用），composer 上方可移除的附件 chip。
- **断线/重连**：模拟条"模拟断线"→ 离线横幅（任务在电脑端继续执行）→"重连"→ 快照恢复、代次+1；"宿主退出"→ 明确"不能继续控制"且无重连按钮；列表页"‹"返回连接页；聊天页"‹"返回列表。
- **键盘/窄屏**：`100dvh` + `env(safe-area-inset-*)` + `visualViewport` resize 兜底，composer 钉在软键盘上方；≤340px 微调。

## WorkBuddy 参考观察（`C:/Downloads/WorkBuddy.apk.1`）

**真实界面运行未验证**：本机无 AVD（`emulator -list-avds` 空）、无 adb 设备、无 system-images；Android SDK 仅有 emulator 与 build-tools，无法安装运行。

静态确认（非界面观察，不作为设计依据之外的推测）：`com.tencent.workbuddy.app` 2.5.0 (650)，Flutter 应用；资产名显示的界面构成——底栏 tab（assistant/automation/expert/knowledge/project/tasks/team）、发送/停止按钮、附件面板（相机/图库/手机文件/ima/腾讯文档/日程）、能力中心（MCP 工具、我的技能）、抽屉（电脑设备、wifi-off 断线状态、新任务）、云会话操作（复制/赞踩/分享/朗读）、`state_load_failed` 加载失败态。

## 截图证据（`shots/`，headless Chrome 实测渲染）

| 文件 | 视口 | 场景 |
|---|---|---|
| `connect-390.png` | 390×844 | 配对码与已配对设备（已撤销置灰） |
| `list-390.png` | 390×844 | 项目/会话列表、状态点、待审批角标 |
| `chat-390.png` | 390×844 | 气泡、工具卡（完成/待审批失效）、流式游标 |
| `approval-390.png` | 390×844 | 底部审批页、当前代次"查看审批"与旧代次失效并存 |
| `offline-390.png` | 390×844 | 断线横幅（休眠文案）、composer 禁用 |
| `chat-320.png` | 320×568 | 超窄屏布局，无横向页面滚动 |

`?scene=list|chat|approval|offline` 是夹具截图入口，只用于无头渲染，非产品行为。

## 未验证边界

- 桌面 Chrome 窄屏验证不等于真机：触摸命中区、真实 IME、Android 返回键/手势、锁屏/切网需真机复核（D6 验收门）。
- `file://` 下软键盘行为不可实测，`visualViewport` 代码路径未在真实移动浏览器验证。
- 交互回归 `tests/Fufucode.M0.Tests/interactive.mjs` 依赖全局 `@playwright/mcp` 捆绑的 playwright 与系统 Chrome；非全仓默认工具链。
- 命令身份/去重、事件游标补齐、审批真实竞争均为占位语义，等 D6 定义接口后替换夹具。
