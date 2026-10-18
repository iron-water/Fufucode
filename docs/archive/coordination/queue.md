# 无人值守工作队列

> 驱动器：`node scripts/orchestrator.mjs --tick`（带锁防重叠）。状态：pending=待派发 / done=完成并合并 / blocked=受阻（见 logs/orchestrator.log） / manual=手动派发（脚本跳过）。
> 规则：agent 只提交到自己分支，**不自行合并 main**；驱动器验证（build/selftest/Core.Tests）全绿后代为合并；FAIL 弹回并标 blocked。

| 轨道 | 任务 | 分支 | worktree | 任务书 | 调度命令 | 状态 |
|---|---|---|---|---|---|---|
| A | 小任务：为 WindowsPasswordVaultCredentialResolver 补真实读写测试（CredWrite→CredRead→删除→环境变量回退验证，加入 SettingsAndDiagnosticsTests）——上轮遗漏 | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-m6core.md + docs/progress.md『A 轨道后续顺序』 | codex | done |
| F | M7 收尾【未完成项，精确指令】：用 ISCC.exe（全路径 C:/Users/21894/AppData/Local/Programs/Inno Setup 6/ISCC.exe）编译 Fufucode.iss 产出安装器，执行安装/卸载演练并把证据写入 progress-f.md——上轮你只改了脚本没有编译 | track-f-packaging | C:/Users/21894/.codex/worktrees/track-f-packaging/Fufucode | docs/archive/assignments/track-f-m7.md | codex | done |
| D | Wave1-4 全合并；Wave3 残余四项复测过+Wave4 F11 一比一对齐（动画/交互/图标/材质，11 项对照过+6 项差异如实）——已合并 main@5cf3ff2 | track-d | C:/Users/21894/.codex/worktrees/track-d-app/Fufucode | docs/archive/assignments/track-d-m6m7.md | manual | manual |
| B | 常驻验证；第三轮（安装版 CLI 冒烟+审批桥包内加载全过，3 项发现分档）——已合并 main@97b665a | track-b | C:/Users/21894/.codex/worktrees/track-b-bridge/Fufucode | docs/archive/assignments/track-b-verify.md | manual | manual |
| A2 | HistoryMapper 去重 helper + realpi 多轮验收场景（M2 收口） | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-m6core.md『本轮派单 2026-09-30』 | codex | done |
 | G | 资产返工（UniGetUI 水准：441 SVG/576 PNG/32 对照图，旧版存档）——已合并 main@38d03b4，用户视觉裁决待定 | track-g | C:/Users/21894/.codex/worktrees/track-g-assets/Fufucode | docs/archive/assignments/track-g-assets.md + 用户返工要求 | codex | done |
| F2 | 安装器升级/回退演练（Maka 派发）——已交付并合并 main@9b27f95，装0.9.0→升1.0.0→完整性→卸载→回退全绿 | track-f-packaging | C:/Users/21894/.codex/worktrees/track-f-packaging/Fufucode | docs/archive/assignments/track-f-m7.md『本轮派单 2026-09-30』 | manual | done |
 | F3 | D Wave2 发现打包四坑（缺 bridge/cmd 丢参/旧包索引坏/快捷方式目录）——已合并 main@3e35436；A6 合并冲突已解、合并包 drill 复测全绿（协调者复核 c7b2c58） | track-f-packaging | C:/Users/21894/.codex/worktrees/track-f-packaging/Fufucode | docs/archive/assignments/track-f-m7.md + docs/archive/progress/progress-d.md『For F』 | manual | done |
 | A6 | CLI 真实验收+打包接线（track a 桌面线程，用户派发）——已合并 main@b7845d6：真实 Pi/bridge 审批双结局、Ctrl+C=130、GUI-CLI 会话锁、launcher 17 项验收；发布接线（CLI publish/PATH/路由）与 bridge 随包 | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-cli.md | desktop-a | done |
 | A3 | CLI 前端第一切片（命令面设计+sessions/chat/models）——已交付并合并 main@1c5ff31，verify 组全绿（驱动器修复缺失 using） | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-cli.md | codex | done |
 | A5 | CLI 第二切片（approve 接管/sessions/settings/models/extensions/diag——无头等价物），另收 D 交接：Pi 写文件后 0xC0000409 崩溃+审批请求留痕——已交付并合并 main@87c9d05，verify 组全绿（232 PASS）；崩溃留痕项待后续 | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-cli.md + cli-design.md『后续』行 | desktop-a | done |
 | H | Codex 新功能研究（codex 内新建 agent 完成，22 项采用判断）——已交付并合并 main@cd4897d | track-h | C:/Users/21894/.codex/worktrees/track-h-study/Fufucode | docs/archive/assignments/track-h-codex-study.md | manual | done |
 | A7 | GUI 组件控制面设计+最小切片（ui snapshot/screenshot/invoke，真实无头控件树 11 项断言）——已合并 main@1d41e22 | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-cli.md 续 | desktop-a | done |
 | A8 | Wave5 契约侧（changes/usage 只读查询+离线报告+通知/fork 核实）——已交付并合并 main@0b3a2e1，全组 272 PASS | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-cli.md 续 | desktop-a | done |
 | D2 | Wave5-R1 五区骨架+左栏+中央——已交付并合并 main@616fcdc（驱动器验收 build/selftest 全绿+截图核对；H-S04/S05 部分覆盖如实）；R2 右栏+底栏已派 | track-d | C:/Users/21894/.codex/worktrees/track-d-app/Fufucode | docs/archive/assignments/track-d-fivezone.md + docs/design/frontend-design-guideline.md | desktop-d | done |
 | D3 | Wave5-R2 右栏 tab 化+底栏 ConPTY 终端+左栏动态状态——已合并 main@b1a49b6 | track-d | C:/Users/21894/.codex/worktrees/track-d-app/Fufucode | docs/archive/assignments/track-d-fivezone-r2.md | desktop-d | done |
 | D4 | Wave5-R3 独立设置+跨区全局（命令面板/用量/通知/自绘Caption）+U01-U09 令牌+H-S01~S09 终审——已合并 main@b1a49b6；D 侧总管线完成，交 A 清单 4 项待派，真实数据终验归协调者 | track-d | C:/Users/21894/.codex/worktrees/track-d-app/Fufucode | docs/archive/assignments/track-d-fivezone-r3.md | desktop-d | done |
 | A12 | Pi 1.0.0 升级切片（track-a-pi10 分支：lock/字符串对齐/272+18+selftest 全绿/1.2.0 安装器）——已合并 main@ffc2ecb，共享 runtime 已切 1.0.0；真实数据快验过（1.0.0 读旧会话 OK）；开发期数据兼容从简（用户指示） | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/operations/pi-upgrade-sop.md ⑥ | desktop-a | done |
 | A9 | Pi 追新基建——已交付并合并 main@a9d55af：探针套件双版本 10/10+自测14、SOP、FakePi 契约化(272 原样 PASS)、0.99.2 差异报告；决策门放行，待派升级切片 | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-pi-upgrade.md | desktop-a | done |
 | A13 | 契约包：get_available_thinking_levels/0xC0000409 于 1.0.0 复现调查+扩展UI应答留痕/sessions project 字段修正/diff patch-hunk 只读契约/fork 元数据与锁迁移——五项均已交付（Core/CLI/FakePi/测试），A13 定向 38 PASS、全组 299 PASS+Pi.Tests 18+App selftest 0 失败、0xC0000409 12 轮实测未复现；UI 组基线既有失败（App nav.chat 重复键，非 A13 引入）已单独记录 | main | C:/Users/21894/.codex/worktrees/track-a-core/Fufucode | docs/archive/assignments/track-a-cli.md 续 + D 交A清单 | desktop-a | done |
 | D5 | Wave5-R4：Markdown+OS toast+队列编辑+实机自验——**四阶段均执行完毕**：阶段1 Markdown 行内渲染完成；阶段2 OS toast 判定为**本机环境限制**（AUMID/开始菜单快捷方式注册不可行）正式结案，取证固化 decisions.md；阶段3 队列条编辑/删除按 Core 能力边界收敛（Core 无单条队列接口、界面本地删除会被 QueueChanged 覆盖复活），界面与命令已就位、待 **A-R4-1**（`TryRemoveQueuedAsync`）一键点亮；阶段4 实机自验部分完成，新发现 **D-R4-1**（窄宽 1024 且右栏抽屉模式下与 composer、标题栏动作区重叠）**单独立项 D6 待排期**，需 G/产品侧先定调两处设计判断。协调者提交 track-d@d8481d6+e130a70，合并 main@fa00505/a0e4caf | track-d | C:/Users/21894/.codex/worktrees/track-d-app/Fufucode | docs/track-d-fivezone-r4.md | desktop-d | done |
 | E1 | 内置浏览器（右栏 Preview 激活，WebView2）——**骨架与自测已交付**：WebView2 宿主 + BrowserView 全链路 + csproj 接线 + PreviewSeam，13 文件 +1392 行；协调者提交 track-e@4478915 并合并 main@e24b33c，build 0 警告 0 错误、`--selftest` 0 项失败。剩余 3 项接线协调项（Preview tab 点亮 / BrowserView 注入 VM / SelfTestRunner 接线）已派 E 轨收口 | track-e | C:/Users/21894/.codex/worktrees/track-e-browser/Fufucode | docs/archive/assignments/track-e-browser.md | desktop-e | done |
 | I1 | 多 agent 协作可视化研究（新建研究轨 track-i）——**阻塞原因误标，已纠正**：原标注『外部阻塞：Codex 账号额度耗尽』**非实际情况**（驱动器把该条误按 `codex` 归属，实为 Qoder 侧承接，本机 Codex 额度与 I1 无因果）；首轮研究已交付（本机参照取证 + 八款参照物盘点 + 信息架构归纳 + 右栏适配方案），协调者提交 track-i@fdcb13d 并合并 main@c5bd739；后续收口（补齐取证缺口 + 设计建议落地）已令 Qoder 侧立刻开工 | track-i | C:/Users/21894/.codex/worktrees/track-i-research/Fufucode | docs/archive/assignments/track-i-multiagent-study.md | manual | pending |

## 协调者处置记录（2026-10-02 夜）

- **E1/D5/I1 三轨产物均已落盘入 main**：三条轨道执行者自 08:44 起全部停滞在「有产出未提交」，本地长期裸放 20+ 文件。协调者按 git 实践逐轨 `git add` → 分支提交 → 合并 main：
  - E1 → track-e@`4478915`（13 文件 +1392）→ main@`e24b33c`
  - D5 → track-d@`d8481d6`（+1214/−4）、收口记录 track-d@`e130a70` → main@`fa00505` / `a0e4caf`
  - I1 → track-i@`fdcb13d`（+259）→ main@`c5bd739`
  - 合并门禁：合并前 E1 分支 build 0 警告 0 错误 / `--selftest` 0 项失败；三方合并后 main 复验 build 0 警告 0 错误 / `--selftest` 0 项失败。
- **`docs/decisions.md` 冲突已解**：HEAD 的 A13 契约段与 track-d 的两段 D 段均为文末追加，按追加日志语义两者顺次保留（A13 → D 阶段2 → D 阶段3）。
- **队列纠错**：I1 的 `blocked` 系误标，已改 `pending` 并注明实际承接方；`调度命令` 由 `codex` 改为 `manual`（实际经 CUA 人工派发）。
- **仍待决**：D6（D-R4-1 窄宽抽屉叠压）排期与两处设计定调；A-R4-1（`TryRemoveQueuedAsync`）接口落地；E1 剩余 3 项接线。 
 | D6 | Wave5-R4 收口——**D 侧全管线完成**：前置+主任务（D-R4-1 避让带 17 断言常驻+PreviewFile 接线）已合并 main@358770a（外部 AI 执行+驱动器修 using/验证/补记录）；真实缩放与 WebView2 渲染归装包终验 | track-d | C:/Users/21894/.codex/worktrees/track-d-app/Fufucode | docs/archive/assignments/track-d-six.md | desktop-d | done |
 | E2 | E1 自测常驻+Files→预览 E 侧入口（TryPreviewFile+PreviewControlName 契约）——已合并 main@a7a5132 | track-e | C:/Users/21894/.codex/worktrees/track-e-browser/Fufucode | docs/archive/assignments/track-e-browser.md | desktop-e | done |
 | F4 | 1.3.0 发布包（五区 UI+Pi 1.0.0+浏览器 Preview）——已交付并合并 main，drill 全绿+孤儿载荷清理教训入账；静默 lnk 存疑/跨版本数据兼容未做(用户口径从简) | track-f-packaging | C:/Users/21894/.codex/worktrees/track-f-packaging/Fufucode | docs/operations/pi-upgrade-sop.md | codex | done |
 | I3 | 契约缺口决策稿——外部 AI 交付+协调者代提，已合并 main@a99b958（缺口②上调 P0：schema v3 已有 parent_session_id；缺口①P0 立项待 A 设计评审；缺口③后置） | track-i | C:/Users/21894/.codex/worktrees/track-i-research/Fufucode | docs/design/multiagent-collab-mvp-spec.md §7 | manual | done |
