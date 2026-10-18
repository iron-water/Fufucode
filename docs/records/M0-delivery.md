# M0 手机原型：交付记录

## M 主区收编交付卡（2026-10-11）

- 任务 M0；接受运行 `3ac17def541e487c8131f9222753f2d5`；需求版本 0；精确来源 `aa7c5747e77d94285b28cd0aec7caf469cdb0765`。accept 与真实 HEAD 一致，原 writer 已停止，原工作树干净。
- 原始起点 `ec824549716c1eb518c8e62aa575111f3240d906`；主区开始 `8be7ba3392ee745d57f562270313ed7aca49d9f2`。
- 真实 no-ff 合并 `37a117114ccd5f5a88392129e7cd3a0af2822b98`，父提交为主区开始与精确来源。合并仅 15 个批准范围文件。
- 仅收编手机 HTML/JS/CSS 夹具、独立静态/浏览器交互测试及任务记录。原型入口是本目录 index.html，脚本引用已完整；不需要改生产共享入口，不接依赖后端。补回 README 中真机不等于桌面模拟的边界说明。
- 主区运行批准脚本 `pwsh -NoProfile -File docs/automation/contracts/M0-verify.ps1 -Repository C:/Projects/Fucode/Fufucode`：exit 0，26 条静态检查通过，App Release 构建 0 警告、0 错误；未更改脚本或断言。
- 另实际运行 `node tests/Fufucode.M0.Tests/interactive.mjs`：exit 0，15 项真实 Chrome 交互通过，包括断线/重连/过期审批不可提交、允许/拒绝正常路径、宿主退出、发送停止。没有用 App 构建代替行为测试。使用已声明的本机全局 Playwright/系统 Chrome，跨机器工具链未验证。
- 证据：`tmp/agent-gate/3ac17def541e487c8131f9222753f2d5.integration-verify.log`、同目录 `.integration-interactive.log`、`.integration-preserve.json`。继承跟踪/未跟踪内容逐文件 SHA-256 校验，未纳入本项提交。
- 未验证：WorkBuddy 真机界面、真实 IME/触摸/返回/锁屏/切网、生产配对/网络/唯一宿主与审批竞争、Avalonia 原生交互。浏览器夹具不是 Android 或 Avalonia 原生验证。
- 状态：M0 批准原型范围已收编 main；G1/G2、D5/D6 与产品整项验收保持未通过。未 push、登录、派工或删除工作树。下一步由外部 M 更新权威记录，在独立授权下处理后续接线。

任务：M0 手机原型（派工难度 L2 常规）｜执行：OMP 受管会话 `prod-readonly-20261010-M0-1`（devin/swe-2，最低成本）。
日期：2026-10-11。依赖 G0：已交付（`docs/records/G0-roadmap-2026-10-10.md`），满足。

## 1. 开工身份卡

| 项 | 值 |
|---|---|
| 工作树 | `C:\Projects\Fucode\worktrees\auto-project-M0` |
| 分支 | `agent/prod-readonly-20261010-M0` |
| 起点 SHA | `ec82454`（main HEAD，无未提交改动） |
| 允许路径 | `docs/frontend-prototype/mobile/*`、`tests/Fufucode.M0.Tests/*`、`docs/records/M0-delivery.md` |
| 未触碰 | 正式外壳、网络宿主、生产源码、权威进度/决策、failure-registry、reference |

## 2. 交付物

| 路径 | 内容 |
|---|---|
| `docs/frontend-prototype/mobile/index.html` | 连接/列表/聊天三屏 + 审批页 + 附件页 + 断线横幅 + 夹具模拟条 |
| `docs/frontend-prototype/mobile/mobile.css` | 窄屏样式：100dvh、safe-area、composer 钉底、≤340px 微调、`[hidden]` 优先级修复 |
| `docs/frontend-prototype/mobile/js/app.js` | 夹具状态机：配对校验、连接代次、流式输出、发送/停止、审批绑定代次、附件 chip、断线/重连/宿主退出、返回栈、`?scene=` 截图入口 |
| `docs/frontend-prototype/mobile/js/fixtures.js` | 全部人工合成数据，逐条 `fixture: true` |
| `docs/frontend-prototype/mobile/README.md` | 打开方式、覆盖界面、WorkBuddy 观察、截图索引、未验证边界 |
| `docs/frontend-prototype/mobile/shots/*.png` | 6 张 headless Chrome 实测渲染截图（390×844 与 320×568） |
| `tests/Fufucode.M0.Tests/` | 独立可执行测试工程（Exe, net10.0），24 条静态契约断言 |

## 3. WorkBuddy 参考观察

真实界面运行**未验证**：`adb devices` 空、`emulator -list-avds` 空、无 system-images，本机无法安装运行 `C:/Downloads/WorkBuddy.apk.1`（281MB）。

静态确认（aapt2 + 资产清单，不作为界面观察）：`com.tencent.workbuddy.app` 2.5.0 (650)，Flutter；底栏 tab、发送/停止按钮、附件面板（相机/图库/手机文件/腾讯文档）、能力中心（MCP/技能）、抽屉（电脑设备/断线态/新任务）、`state_load_failed` 加载失败态。据此把断线态、设备管理、附件面板、发送/停止纳入原型范围。

## 4. 验证命令与结果

- `dotnet run --project tests/Fufucode.M0.Tests` → 24/24 PASS，最终复跑仍 ALL PASS。
- `node --check js/app.js && node --check js/fixtures.js` → 语法通过。
- Chrome DevTools MCP，视口模拟 `390x844x3,mobile,touch` 与 `320x568x2,mobile,touch`，`evaluate_script` 实测交互：
  - 错误配对码 → "配对码错误或已过期"；正确码 `426810` → 列表屏，3 会话 2 项目，hostStatus"已连接"。
  - 会话 s1 → 旧代次审批显示"已失效"，不再出现可点按钮（修复后复核）。
  - 发送 → 流式逐块输出、发送隐藏/停止显示；停止 → "已发送停止（clear_queue + abort）：运行中断，未执行队列不自动重发"，系统消息跨 renderTimeline 持久。
  - 流式结束 → 当前代次待审批 → 审批页 → 允许 → 工具卡转"已完成"。
  - 附件页 → 两个 chip → 移除一个剩一个 → 仅附件发送 → "（仅附件）📎 …"。
  - 模拟断线 → 横幅"电脑无响应：可能已休眠，任务在电脑端继续执行"，发送/输入禁用；重连 → 代次 2、快照恢复、"旧审批已失效"；宿主退出 → "后台宿主已退出，不能继续控制"且无重连按钮。
  - 返回链 chat→list→connect 实测。
  - 320×568 实测：composer 顶边 y=480 在视口内、无横向页面滚动。
- headless Chrome `--screenshot` 渲染 6 张证据截图并逐张目视复核。

## 5. 验证中发现并修复的缺陷（本任务内登记，不写全局 failure-registry）

| # | 现象 | 修复 |
|---|---|---|
| M0-1 | `[hidden]` 被 `.banner`/`.sheet` 的 `display:flex` 覆盖，断线横幅与审批页恒显 | `mobile.css` 加 `[hidden]{display:none!important}` |
| M0-2 | 连接成功后列表页 hostStatus 仍"离线"（`setConn` 在 connect 屏提前返回） | `show()` 换屏后重放连接视觉 |
| M0-3 | 旧代次审批仍显示可点"查看审批"；系统消息不入模型被 renderTimeline 抹掉 | `renderMsg`/`openApproval` 按代次判定；`sysMsg` 入 session.messages |

## 6. 未验证 / 阻塞

- WorkBuddy 真实界面运行（无设备/AVD/系统镜像）——仅静态资产观察，标未验证。
- 真机验收：触摸命中区、真实 IME、Android 返回/手势、锁屏/切网/重连——归 D6 验收门，桌面模拟不称真机。
- `file://` 下软键盘行为不可实测；`visualViewport` 路径代码存在但未在真实移动浏览器验证。
- 命令身份/去重、事件游标补齐、审批真实竞争、唯一宿主写入均为占位语义，待 D6/D5 接口。

## 7. 交付卡

```
交付卡
任务ID与软件：M0｜OMP 受管会话 prod-readonly-20261010-M0-1（devin/swe-2）
工作树：C:\Projects\Fucode\worktrees\auto-project-M0｜agent/prod-readonly-20261010-M0｜起点 ec82454
本任务提交：031588e（原型+夹具+测试+截图）；本记录提交见 git log（docs/records/M0-delivery.md）
改动文件：docs/frontend-prototype/mobile/**（13 文件）、tests/Fufucode.M0.Tests/**（2 文件）；未继承用户改动
实际验证：M0.Tests 24/24 PASS 两次；DevTools 窄屏模拟交互全流程实测；6 张截图目视复核；node --check 语法通过
未运行：WorkBuddy 真机界面（无设备/AVD）；真机 IME/返回/锁屏切网（D6 门）；Avalonia 构建（无源码改动）
公共接线需求：原型仅夹具；真实配对/传输/命令身份/事件补齐/审批契约待 D5+D6 定义后替换夹具
状态：模块交付待接线
原会话是否仍在写代码：否
```

## 8. 回修记录 M0-R1（2026-10-11，run prod-readonly-20261010-M0-2）

reviewer finding（review.json, runId f3d9f488）：`resolveApproval` 提交时不复核连接/审批存在性/代次/调用身份——审批弹窗打开时模拟重连，旧代次审批仍被"允许一次"置为已完成。

修复（`js/app.js`）：
- `openApproval` 快照 `{id, callId, generation}` 身份。
- `resolveApproval` 提交时复核 `conn==="connected"`、审批仍存在、`generation` 未变、`callId` 一致；任一不符即不生效并提示"审批已失效，本次允许/拒绝未生效"。
- `setConn` 离开 connected（断线/重连中/宿主退出）即 `invalidateOpenApproval` 关闭弹窗。
- 工具卡状态更新按 `approvalId + callId` 双重匹配。

新增实际交互回归 `tests/Fufucode.M0.Tests/interactive.mjs`（Playwright + 系统 Chrome，15 断言）：弹窗开时断线→弹窗关闭卡片保持待审批；复刻 reviewer 竞态（弹窗开时重连再强制提交允许）→卡片不转已完成、无"已允许本次执行"；允许/拒绝正常路径；宿主退出关弹窗禁重连；连接→列表→发送→停止全流。同时 `Program.cs` 增加 `approval-commit-time-revalidation`、`approval-modal-invalidation` 静态断言（26 条）。

回修验证：`dotnet run --project tests/Fufucode.M0.Tests` 26/26 PASS；`node tests/Fufucode.M0.Tests/interactive.mjs` 15/15 ALL INTERACTIVE PASS。
