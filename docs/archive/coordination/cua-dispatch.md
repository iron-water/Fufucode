# Cua-Driver GUI 派发手册（协调者/轨道通用）

> 沉淀自 2026-09-29/30 实测。用途：给**无 CLI 的 GUI agent**（devin TUI、Maka Desktop、codex desktop）派发任务指令，替代手动粘贴。

## 工作原理

```
node scripts/cua-dispatch.mjs
  ├─ cua-driver list_windows            （守护未跑 → 自动 spawn `serve`）
  ├─ 按窗口标题子串找 pid + window_id
  ├─ bring_to_front                      （解除最小化；Cascadia/Electron 截图与前台投递的前置）
  ├─ type_text(delivery_mode=background) → 若回 background_unavailable 自动升级 foreground(SendInput)
  ├─ press_key(return, foreground)       （发送）
  └─ 等待 N 秒 → get_window_state(include_screenshot) 回执截图存 tmp/
```

## 各目标的实测通道

| 目标 | 窗口类 | 投递路径 | 备注 |
|---|---|---|---|
| devin（Windows Terminal 页） | CASCADIA_HOSTING_WINDOW_CLASS | type_text + return，**必须 foreground**；PostMessage 被丢 | 派发前需窗口在前台（脚本自动 bring_to_front）；真机派发已验证（D Wave1 合并指令 09-30 派达） |
| Maka（Electron） | Chrome_WidgetWin_1 | **clipboard_write + 前台 Ctrl+V + 发送按钮**（实测成立：SendInput 的 type_text 落入但网页输入框不显示——Electron 合成输入需合成器路径；`--input paste --click-at x,y`） | UIA 输入框不可寻址（主内容 Document 未暴露输入元素），聚焦用窗口局部坐标点击；发送按钮在输入框右下 |
| codex desktop（Electron） | 同上 | 同上 | 未实测；优先 `codex exec` CLI（orchestrator 已用） |
| 记事本等 UWP/XAML | Windows.UI.* | type_text 后台走 UIA ValuePattern | 后台零抢焦点已验证 |

## 参数

```
node scripts/cua-dispatch.mjs --window <标题子串> --text "<指令>" [--send enter|none] [--poll 45] [--shot tmp/x.png]
node scripts/cua-dispatch.mjs --pid 53464 --window-id 132262 --text-file docs/dispatch-d.txt --send none   # 只投递不回车
node scripts/cua-dispatch.mjs --window "Maka" --input paste --click-at 885,910 --text-file docs/dispatch-b.txt
```

- `--input paste --click-at x,y`：Electron 专用——剪贴板注入 + 前台点击聚焦输入框（坐标按当次截图量）+ Ctrl+V。
- `--send none`：文本进输入框但不发送，留给人工/下一条指令确认后发。
- 文本 >8KB 自动走 stdin（Windows 命令行长度限制）。
- 回执截图默认 `tmp/dispatch-shot.png`——**每次派发后目检截图确认文本落点**（UIA 的 unverifiable 不等于失败，也不等于成功）。

## 硬规则（实测踩坑）

1. **每个 UIA 动作前重拍快照**：`get_window_state` 产生新 snapshot 使旧 `element_token` 立即 stale。脚本不用 token（type/press 是 pid 级全局输入），但若你改用 `click`/`set_value`/`invoke_menu`，必须拿**当次**快照的元素。
2. **打包应用用 AUMID 启动**：`launch_app {name:"Notepad"}` 会命中 System32 的 7KB 桩进程即死；用 `aumid:"Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"`。派发只做注入，不启动目标——目标由用户开。
3. **hotkey 对 XAML/Cascadia 不可靠**：Ctrl+V/Alt+F4 在 WinUI3 上只认 UIA AcceleratorKey；发送一律 `press_key key:"return" delivery_mode:"foreground"`。粘贴（Ctrl+V）先别用——实测把 chord 只落了一个 "v" 字符。
4. **kill_app 只能杀驱动器自启进程**；清理别指望它。
5. **派发窗口期勿抢键盘**：foreground 投递期间（<1s）用户按键可能混入文本。投递前后截图核对。
6. **bash 里 `\\.\pipe\` 反斜杠会被吞**：手动起守护写 ps1 用 `pwsh -File`，或直接用本脚本（自启逻辑已绕开）。
7. 派发文本**单行**最稳：终端 TUI 对多行粘贴处理不一；指令写成一段。
8. **信息获取≠前台（2026-09-30 用户投诉后补）**：被派 agent 抓目标应用界面时，一律先走 `get_window_state` 的后台 UIA 树 + 窗口截图——窗口在后台/被遮挡也能读，**不需要 bring_to_front**。启动被测应用用 `launch_app`（Windows 上 `SW_SHOWNOACTIVATE`，不抢焦点），禁止 `start`/双击。只有确实需要可见像素证据（审美验收）才允许短暂前台化，完事立即恢复原前台。D Wave2 期间反复前置 Fufucode 窗口干扰用户——此类行为一律禁止，今后派发给 devin 的任务书必须显式写入本规则。

## 派发节奏铁律（本轮踩出来的）

**派发行入队后必须先等"任务书进 main → 目标 worktree 合并 main"再跑 tick**。orchestrator 派发的是 worktree 里的 agent，它只认自己 worktree 的文件；任务书只在 main 上而 worktree 没合并时，agent 会正确 BLOCKED 但驱动器会把"写了进度说明"误判为产出、假标 done。已两次实踩（A3 先入队后补任务书 → 同此陷阱）。正确顺序：写任务书 → 入队 → main 合并 → 目标 worktree merge main（驱动器可代）→ 再 `--tick`。

## 接入 orchestrator（可选演进）

queue.md 的 `调度命令` 列已预留：`codex`→orchestrator 自动派；`manual`→脚本跳过。可把 manual 行改为
`cua-dispatch --window "<窗口标题>"`，让 `--tick` 顺手完成 GUI 派发——**前提是人工确认该标题此刻对应正确目标**（误投到别的聊天窗会泄密任务文本）。保守起见 manual 行暂不改，由协调者跑本脚本。
