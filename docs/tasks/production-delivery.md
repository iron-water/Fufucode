# 任务书：Fufucode 生产级交付收口

派发日期：2026-10-08。派发者：用户（经 ZCode 起草）。
接收者：主 agent（Codex Desktop 项目线程）。
**用户本人在执行期间不在场**，见 §6 自主决断条款。

---

## 1. 使命

把 Fufucode 从"功能基本可用、界面已统一"推进到**生产级可交付**：所有已声明功能真实可用，无占位入口；打包链路在干净机器上能跑通；全量验证门禁全绿；文档与交付记录与代码一致。

完成后的判定标准（全部满足才算交付）：

1. `dotnet build Fufucode.sln` 0 警告 0 错误。
2. App 全量 `--selftest`（不带 `-only`）**0 失败**。
3. Core.Tests、Pi.Tests 全量通过（数字写进记录，不许引用历史）。
4. 安装包在**本机**（`C:\Projects\Fufucode`，Windows x64）能构建出来，并在干净环境验证安装、启动、卸载。
5. 仓库内**不存在**指向其他机器或旧 worktree 的硬编码路径（见 §3 已知阻塞）。
6. `docs/records/` 有本轮交付记录，`docs/decisions.md` 有本轮决策，`docs/progress.md` 有本轮进度条目。

---

## 2. 范围

### 2.1 必须完成（用户已明确要求）

| # | 项目 | 要求 | 验收 |
|---|---|---|---|
| A | **Codex 式会话管理** | 按 `docs/人类意见/左辅助和右辅助的意见.md` 的截图与描述，把"会话管理"页做成 Codex Desktop 那套：四个最高阶分类（置顶 / 分区 / 项目 / 会话）；分区与项目各自有二级菜单；分叉有"创建聊天分支"与"在新工作树中创建分支"两项；复制有"深度链接 / 复制为 markdown / 复制工作目录"三项；悬停显示状态栏（仅置顶与归档）；项目可右键编辑（改文件夹与名称）；分区可右键改名；活动视图的优先级有二级菜单 | 与参考截图逐项对齐，每条有真实行为 |
| B | **全量自测归零** | 当前 28 项失败（W5-R2 10 / W5-D6 10 / W5-R3 8），全部是旧断言假设（32DIP caption、旧容器宽度、已删除的审查入口等）。逐条判断：**是产品回归就修产品，是断言过时就更新断言并写明理由**。不得放宽断言制造通过 | 全量 0 失败，每条改动在记录里注明归属 |
| C | **打包链路可在本机跑通** | `scripts/packaging/*.ps1` 里硬编码了 `C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode` 与 `C:\Users\21894\AppData\Local\Programs\Inno Setup 6\ISCC.exe`。改为从脚本位置推导仓库根、从 PATH 或常见安装位置探测 ISCC；`orchestrator.mjs` 里同样有旧路径 | 在本仓库执行打包全流程，产出 setup.exe，`integrity.sha256.json` 校验通过 |
| D | **占位入口清零** | 当前占位：导出 PDF、在新工作树中创建分支、复制深度链接（三者带"尚未实现"禁用原因）；完整诊断包导出（设置里禁用按钮） | 要么真实实现，要么从界面移除并记入未做项。不许留禁用按钮当装饰 |
| E | **Windows 系统通知** | 浮窗的"Windows 系统通知"渠道目前只存设置不投递。未打包 Win32 应用需注册 AppUserModelID（写注册表或快捷方式）并接入通知 API | 真实能弹系统通知；失败时降级到应用内浮层并在设置里如实说明 |
| F | **浮窗补完** | 状态行（连接/Shell/活动/Token）与快捷动作（用量、轨迹、朗读、变更摘要）已完成；补：用量详情与调用轨迹按 `docs/人类意见` 建议增强（分别参考 maka 与 deepseek harness / zcode 的实现）；朗读补"开始朗读"以外的可用性 | 每项有真实行为与验证 |
| G | **模型认证管理** | 现在是跳设置页的网络分类。做成独立二级窗口，内容由你按 Pi 实际能力定义（凭据列表、环境变量、添加/移除），但**不得虚构 Pi 不存在的凭据管理 RPC** | 窗口有真实数据来源 |

### 2.2 可选（有余力再做，做前先记账）

- 多窗口（R02 研究已结论：方案 A 同进程多顶级窗口，`SessionLease` 已跨进程安全）。仅在 A–G 全绿后再启动。
- 三种视觉风格预设深化（R04 结论：token 层可实现，不改结构）。
- 浮动摘要卡、消息操作增强等 Codex 对标细节。

### 2.3 明确不做

- 不移植 Pi 内核、不自研模型供应商协议、不自研终端模拟器。
- 不做远程 RPC 服务器、后台系统服务、自研沙箱。
- 不改 Pi 运行时版本（当前仓库内钉 1.0.0）；全局 npm 的 `pi` 1.1.0 与本产品无关。
- 不推送远端、不改写历史（除非用户在后续会话明确要求）。

---

## 3. 已知阻塞与现场事实

1. **硬编码路径**：`scripts/packaging/build-installer-version.ps1`、`run-drill.ps1`、`scripts/orchestrator.mjs` 里有 `C:\Users\21894\...`。本机是 `C:\Projects\Fufucode`，`C:\Users\21894` 不存在。这是 D 项之前的硬阻塞。
2. **全量自测 28 失败**：清单见 `tmp/full-suite.log`（如已清理，重跑 `dotnet tmp/<build>/Fufucode.App.dll --selftest` 复现）。旧记录已注明"旧容器/宽度/32px caption 假设"。
3. **Pi 运行时**：仓库内 `runtime/pi` 钉 `@earendil-works/pi-coding-agent@1.0.0`，`runtime/node/node.exe` 为托管 Node。应用只解析仓库内路径。
4. **本机无全局 node PATH**：脚本调用 node 时用 `runtime/node/node.exe`（见 `docs/runtime-baseline.md` 与既有脚本写法），不得假设裸 `node` 可用。
5. **未收口的历史项**：`docs/records/failure-registry.md` 的 R-004（探针工装退出码/NodeExe 继承）、R-005（bridge-verify 结果落点）、R-101（1.3.0 包四项未验）、R-102（多 agent 契约缺口待设计评审）。R-004/R-005 顺手清掉；R-101 在 C 项完成后逐条复核；R-102 保持 open 并在记录里说明。
6. **凭据**：应用侧模型配置读环境变量 `FUFUCODE_ATRIA_API_KEY`（本机已设）。真实模型验证可用它；不得把密钥写进任何文件或记录。
7. **界面基线**：设置页已对齐 Windows 内置设置（度量级，见 `docs/records/settings-windows-parity-2026-10-08.md`）；左右辅助栏已按用户意见重做（见 `docs/records/auxiliary-bars-2026-10-08.md`）。这两处**不要推翻重做**，只在明确缺陷上增量修。

---

## 4. 纪律（硬约束，来自 AGENTS.md 与 AI_DEVELOPMENT_PLAN.md）

- **唯一常驻工作区**：`C:\Projects\Fufucode`，分支 `main`。`reference/` 下第三方克隆保持只读。
- **小步提交**：每个可验证增量一次提交；提交只含本次任务内容。
- **验证门前置**：实施前写清本次验收条件；提交前运行与风险相称的验证。纯文档改动只核对差异与格式。
- **不得伪造**：未实际运行的验证不得标为通过；不虚构 RPC 命令、协议能力或模型行为（以 `docs/runtime-baseline.md` 实测为准）。
- **不得放宽断言**：测试失败时区分"产品回归"与"断言过时"。更新断言必须写明旧假设为何不再成立。
- **记录同步**：交付记录进 `docs/records/`，决策进 `docs/decisions.md`，进度进 `docs/progress.md`。
- **中文回答，简洁干练**；先给结论，再给依据。

---

## 5. 模型派单策略（省额度）

用户额度有限，可把这几个模型（均在 `magpie/group/auto-*`，除 swe-2）作为执行资源，**按智力从高到低优先使用**：

| 优先 | 模型 | 适合的任务 |
|---|---|---|
| 1 | `magpie/group/auto-hy4-preview-f` | 架构判断、跨模块设计、难 bug、A 项会话管理设计 |
| 2 | `magpie/group/auto-deepseek-v4-1-flash` | 主力实现：C/D/E/F/G 的实现与调试 |
| 3 | `magpie/group/auto-glm-5-3-flash` | 常规实现、B 项断言逐条核对、文档整理 |
| 4 | `magpie/group/auto-gemini-3-8-flash` | 辅助：批量机械改动、格式统一、重复性核对 |
| 5 | `magpie/devin/swe-2` | **免费垫底**：低风险批量工作（文案统一、清单核对、证据归档） |

派单规则：

- **默认自己（主 agent）做核心与跨文件改动**：会话管理、打包链路、通知接入这类跨模块工作，派出去来回沟通的成本高于自己做。
- **派出去的活必须自包含**：目标、允许改的路径、验收条件、证据要求、硬约束，一次写全；不要让执行模型再来问范围。
- **一包一验证**：执行模型交付后由你收编、独立复核（读代码 + 跑与风险相称的验证），**不采信"我改好了"的口头结论**。
- **同一文件不并行**：多个执行模型不得同时改同一文件；大任务按波拆，波内串行。
- **省额度**：能用免费 swe-2 做的机械活不占高智力模型；高智力模型只投在真有判断难度的点上。
- **每次派单在 `docs/progress.md` 留一行**：派给谁、做什么、结果如何。

---

## 6. 自主决断条款（用户不在场）

用户已明确：**这段时间人不在，让它自己决断，千万不要停。**

具体含义：

1. **不要停下来问**。没有用户的确认也要往前走。遇到设计空白（例如认证窗口放什么、深度链接格式），按"最保守可用"实现，并在记录里写明假设，后续可改。
2. **只有真外部阻塞才停**：需要用户本人完成的登录/授权、需要付费、需要用户决策的产品方向。遇到这类，**跳过它继续做其他项**，把阻塞写进记录，不要整条流水线停下。
3. **不确定时的顺序**：先做能自证的（B 断言、C 打包、D 占位清理），再做需要外部条件的（E 通知注册、G 认证窗口），最后做大的（A 会话管理）。
4. **每完成一项就提交并记录**，不要攒到最后一起交。
5. **发现新问题**：登记到 `docs/records/failure-registry.md`，能顺手修就修，修不了标 open 并写清复现。
6. **结尾要给一份总结**：做了什么、验证了什么、没做什么、为什么、下一步建议。用事实说话，不夸大。

---

## 7. 关键文件索引

| 用途 | 路径 |
|---|---|
| 协作纪律 | `AGENTS.md` |
| 权威开发计划 | `docs/AI_DEVELOPMENT_PLAN.md` |
| 运行基线（Pi/RPC 实测） | `docs/runtime-baseline.md` |
| 用户意见原文 | `docs/人类意见/左辅助和右辅助的意见.md` |
| 视觉/交互交接（已完成部分） | `docs/design/visual-unification-brief.md` |
| 已交付记录 | `docs/records/`（最新几篇是本轮基线，勿推翻） |
| 决策留档 | `docs/decisions.md` |
| 失败登记册 | `docs/records/failure-registry.md` |
| 打包脚本 | `scripts/packaging/`（含硬编码路径，待修） |
| 自测入口 | `src/Fufucode.App/SelfTest/SelfTestRunner.cs` |
| 会话管理页 | `src/Fufucode.App/Views/OverviewView.axaml` + `MainWindow.SessionManagement.cs` |
| 浮窗 | `src/Fufucode.App/Views/FloatingPanelView.axaml` + `MainWindow.Floating.cs` |
| 辅助栏设置 | `src/Fufucode.App/Views/WorkbenchSectionsView.cs`（`RenderAuxiliary`） |

---

## 8. 验证命令速查

```
# 构建
dotnet build C:\Projects\Fufucode\Fufucode.sln --no-restore -v quiet

# 构建到独立目录（避免锁定）
dotnet build src\Fufucode.App\Fufucode.App.csproj --no-restore -v quiet -o tmp\<name>-build

# 定向自测
dotnet tmp\<name>-build\Fufucode.App.dll --selftest --r05-only          # 期望 0 失败
dotnet tmp\<name>-build\Fufucode.App.dll --selftest --tabs-only         # 期望 0 失败
dotnet tmp\<name>-build\Fufucode.App.dll --selftest --settings-destination-only  # 期望 0 失败

# 全量自测（B 项归零目标）
dotnet tmp\<name>-build\Fufucode.App.dll --selftest

# 看界面
start tmp\<name>-build\Fufucode.App.exe --mock          # 演示数据
start tmp\<name>-build\Fufucode.App.exe --project <路径> # 真实 Pi

# 打包（修好硬编码路径后）
powershell -File scripts\packaging\package.ps1
```

注意：构建输出目录若被运行中的应用锁定，先 `taskkill //IM Fufucode.App.exe //F` 再构建。

---

## 9. 交付物清单

1. 代码：A–G 各项的实现提交。
2. 记录：`docs/records/production-delivery-<日期>.md`，含每项的验证命令与结果、未做项与理由。
3. 决策：`docs/decisions.md` 追加本轮要点。
4. 安装包：`out/Fufucode-<版本>-setup.exe` + 校验值。
5. 总结：最终一条给用户的中文总结。
