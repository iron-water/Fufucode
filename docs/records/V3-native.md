# V3 原生补验交付记录

日期：2026-10-11。任务书：`docs/tasks/hourly-V3-native.md`（L2，测试 lead `devin/swe-2`）。
边界：`docs/tasks/execution-contract.md`、`docs/tasks/session-handoff-protocol.md`、computer-use 技能。

## 0. 开工身份卡

| 项 | 值 |
|---|---|
| cwd / 工作树 | `C:\Users\mio\.codex\worktrees\cf95\Fufucode` |
| Git 状态 | detached HEAD（未同步 main、未集成） |
| 固定产品 HEAD | `de69446ce7803e3575621c9aeec9064c68cb8329`（`feat: route library citations into visible main chat draft`） |
| 起点 SHA | `504fcc3342d92c513f955f7b2acade427f0c6470`（任务书提交） |
| 允许路径 | `docs/records/V3-native.md`（本文件）、`tmp/v3-native/` 夹具 |
| 模型纪律 | 零云模型调用；本机 HTTP 合成流；真实 Pi/OMP CLI 进程；`devin/swe-2` 免费档 |
| 原生工具 | `node_repl` + `@oai/sky`（computer-use 技能），真实窗口坐标/AX 点击 |

## 1. 结论摘要

- **V3 原生补验：通过**（接线行为在真实窗口、真实 Pi 进程上取证）。
- 验证 `de69446` 的接线：资料引用 → 当前可见主聊天草稿，含来源/版本/原文一致性、草稿保护/恢复、会话切换路由（含回读 A 草稿）、非聊天页不可达、死端点与缺后端可执行文件两种失败面、关闭重开不残留重复订阅。
- **本会话整体状态：验证通过 / 提交待验收。不自称 V3 整项验收（V1/V2 已由 P1 会话分别验收）。**

## 2. 原生取证工具与已知限制

- `sky.list_apps()` / `get_window` / `get_window_state` / `click` / `type_text` / `press_key`。
- Avalonia 元素索引点击在弹窗/菜单场景偶发 `element NNN is not available in cached app state`；改用「最新 screenshotId + 窗口相对坐标」。
- 命令面板（`Ctrl+K`）输入「资料库」可稳定打开资料页；dock tab「+」弹窗坐标不稳定。
- `sky.drag` 不可用（`from.x must be a finite number`）。
- 系统文件对话框输入完整路径可导入文件（`OpenFilePickerAsync` 在独立 popup 窗口，不在 `list_windows` 中，但 `type_text` 可写入文件名框）。

夹具（本机 HTTP 合成流，零云调用）：

```powershell
$env:D1_PI_NODE='C:/Projects/Fucode/Fufucode/runtime/node/node.exe'
$env:D1_PI_CLI='C:/Projects/Fucode/Fufucode/runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'
$env:D1_OMP_EXE='C:/Users/mio/.bun/bin/omp.exe'
node tmp/v3-native/fixture.mjs      # 正常端点（v3fx/v3fx）
node tmp/v3-native/fixture-dead.mjs # 死端点（v3dead/v3dead，http://127.0.0.1:1）
node tmp/v3-native/fixture-missing.mjs # 缺后端 exe（v3missing/v3missing，端点正常但 --pi-node/--pi-cli/--omp-exe 指向不存在路径）
```

数据根：`tmp/v3-native/data`（正常/死端点共用）、`tmp/v3-native/data-missing`（缺后端）；项目根：`tmp/v3-native/project`、`tmp/v3-native/project-missing`。

## 3. 验证结果（逐项）

| # | 场景 | 操作 | 证据 | 结果 |
|---|---|---|---|---|
| 1 | 真实导入/选中/生成引用 → 主聊天草稿 | 文件对话框导入 `sample-note.txt` → 选中条目 → 点「生成引用」 | InputBox Value = `> 资料《sample-note.txt》· 来源：…\sample-note.txt · 版本 92320fc0 > V3 sample document line one. > Second line with keyword V3NEEDLE-77 inside. > Third line. >`；时间线通知「资料引用已插入当前主聊天草稿，尚未发送。」；发送按钮启用但未自动发送 | **通过** |
| 2 | 引用内容一致性 | 读剪贴板 `Get-Clipboard` | 剪贴板原文与 InputBox Value 完全一致（来源路径、版本 92320fc0、3 行正文） | **通过** |
| 3 | 未选中不投递 | 打开资料页但不点条目 → 点「生成引用」 | 剪贴板保持 `SENTINEL-V3-REOPEN`，InputBox 无写入，无通知 | **通过** |
| 4 | 草稿保护 | 输入 `EXISTING-DRAFT-DO-NOT-CLOBBER` → 点「生成引用」 | InputBox 仍为 `EXISTING-DRAFT-DO-NOT-CLOBBER`；通知「当前主聊天已有草稿；资料引用已保存为待恢复文本，未覆盖草稿」；出现「恢复已清空的排队文本（5 行）」 | **通过** |
| 5 | 引用恢复 | 点「恢复」按钮 | InputBox = `EXISTING-DRAFT-DO-NOT-CLOBBER` + 引用全文追加 | **通过** |
| 6 | 会话切换只进新当前主聊天（A→B→回读 A） | A(`5262952e`) 草稿 `EXISTING-DRAFT-DO-NOT-CLOBBER`+已恢复引用 → 切 B(`9fea2431`) → 资料库选中 `sample-note.txt` → 生成引用 → 回 A | B 的 InputBox 收到引用（剪贴板比对逐字一致）；回 A 后 InputBox 全文与切换前逐字一致（`EXISTING-DRAFT-DO-NOT-CLOBBER`+同一份引用），无第二份追加、无覆盖；全程无侧聊 dock 打开，引用未流向侧聊 | **通过** |
| 7 | 非聊天页只复制 | 切到设置页 → `Ctrl+K` 搜「资料库」 | 命令项 `IsEnabled = False`，DisabledReason = `返回工作区后可使用`；无生成入口，不投递 | **通过** |
| 8a | 死端点发送失败不回退 | 把 settings.json 改为 `http://127.0.0.1:1` 死端点 → 重启 → 发送引用 | 时间线出现「模型失败 Connection error.」；InputBox 保留草稿；无静默回退到其他后端；无新会话产生 | **通过** |
| 8b | 缺后端可执行文件（区别于连接失败） | `--pi-node C:\nonexistent\node.exe --pi-cli C:\nonexistent\cli.js --omp-exe C:\nonexistent\omp.exe` + 正常本地端点启动（fixture-missing.mjs，UI PID 38800） | 应用未进入工作区：启动页直接显示「启动失败：Pi runtime missing. Configure --pi-node and --pi-cli with the managed runtime paths.」与「重试 / 打开其他项目」。与 8a 的「Connection error」为不同错误面（`RealSessionBootstrap.ResolveRuntime` 的 `FileNotFoundException` 分支）；无会话创建、无引用投递、无静默回退 | **通过** |
| 9 | 关闭/重开资料页不残留重复订阅 | 关闭资料库 dock tab → `Ctrl+K` 重开 → 选中条目 → 生成引用 | ax 树中 `dock-tab-library` 仅出现 1 次；生成引用仍只触发 1 次通知+1 次插入（非双份） | **通过** |

## 4. 实际运行的验证命令

```text
dotnet build src/Fufucode.App -v quiet   → 已成功生成，0 个警告，0 个错误（18.44 s）
node tmp/v3-native/fixture.mjs           → UI PID 12296
node tmp/v3-native/fixture-dead.mjs      → UI PID 36656
node tmp/v3-native/fixture.mjs（重启）    → UI PID 45988（A→B→回读 A 取证）
node tmp/v3-native/fixture-missing.mjs   → UI PID 38800（缺 exe 错误面）
Set-Clipboard "SENTINEL-*" + Get-Clipboard → 引用写入归属可证
```

## 5. 未验证项（如实标注）

- 真实 IME 组合态输入。
- 系统 DPI / 缩放非 100% 下的布局与坐标命中。
- 多显示器、窗口跨屏拖拽后的布局。
- 真实云端模型试跑（全部为本机合成夹具；Pi/OMP 为真实 CLI 进程但模型响应为本地流）。
- WebView2 真实页面布局。
- 引用（非副本）导入模式、网页链接与文本片段导入的原生端到端。
- 侧边聊天（`sidechat` dock）未作为 V3 目标验证；当前接线默认投当前主聊天，侧聊未接收。
- 「导出选中」「导入资料包」「重建索引」按钮未点（属资料库模块既有功能，P1 已验收）。

## 6. 交付卡

| 项 | 内容 |
|---|---|
| 任务 | V3 原生补验（`docs/tasks/hourly-V3-native.md`，L2） |
| 状态 | **验证通过 / 提交待验收**（非整项验收） |
| 工作树 | `C:\Users\mio\.codex\worktrees\cf95\Fufucode`，detached HEAD |
| 固定产品 HEAD | `de69446ce7803e3575621c9aeec9064c68cb8329` |
| 验证范围 | 资料引用→主聊天草稿接线、草稿保护/恢复、会话切换路由（含回读 A）、非聊天页不可达、死端点+缺 exe 两种失败面、生命周期 |
| 变更文件 | `docs/records/V3-native.md`（本文件）；`tmp/v3-native/` 夹具（不入 Git） |
| 验证 | build 0 警告 0 错误；10/10 场景原生通过 |
| 未验证 | 见 §5（8 项） |
| 未做 | 未修改产品代码、未同步/集成 main、未 push、未清理 worktree、未派下一任务 |
| 需要协调 | 否 — V3 接线行为符合 `M-V3-citation-2026-10-11` 设计 |

## 7. 停止写入声明

本会话已完成交付记录与提交，**停止写入**：不再修改代码、不再提交、不再动工作树状态（含不清理 `tmp/v3-native/` 夹具目录）。等待原会话/主控的返工或验收指令。
