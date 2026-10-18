# 生产级交付收口（2026-10-08）

任务书：`docs/tasks/production-delivery.md`。状态：进行中，尚未达到六项交付门禁。

## 验收与执行

- 顺序 B → C → D → E/F/G → A；设置 Windows 对齐和辅助栏基线仅增量修复。
- 最终必须：solution 零警告/错误、App 全量零失败、Core/Pi 全量、安装包及干净 Windows 安装启动卸载、运行代码无旧机器路径、记录同步。
- 设计空白按最保守可用实现；外部阻塞单项登记，继续其他项目。未实测不记通过。
- 无派单：核心及跨文件由主 agent 实施，不使用工具不支持的模型名称。

## B 基线与逐条分类

首次独立构建：`dotnet build src/Fufucode.App/Fufucode.App.csproj --no-restore -v quiet -o tmp/production-build`，0 错误、3 警告。
首次全量：`dotnet tmp/production-build/Fufucode.App.dll --selftest`，14 失败（R2 5 / D6 5 / R3 4），并非历史的 28；以本次复现为准。

| 失败断言 | 分类 | 依据与修复 |
|---|---|---|
| R2 Changes mock 投影 | 过时 | 通用停靠默认右宽 320，不再固定 ≥360；检查实际宽与偏好一致，保留四条 mock 数据要求 |
| R2 底栏双实例限高 | 过时 | 旧测试只显示 VM，不打开通用停靠页面；改真实 OpenDockPage。按工作区保留中央 360 DIP，检查双实例、偏好尺寸、中央剩余高度 |
| R2 1200 宽度身份 | 过时 | 抽屉断点现为 1100，不是 1202；检查并排、宽度和对象身份 |
| R2 800 宽度身份 | 过时 | 窄窗默认不主动打开抽屉；真实 toggle.right 显式打开，检查宽度和身份 |
| R2 1600 宽度身份 | 过时 | 可配置宽度非固定 ≥360；检查恢复并排后的偏好和身份 |
| D6 1440 并排 | 过时 | 右栏按配置尺寸；无避让边距要求保持 |
| D6 1024 避让带 | 过时 | 用户基线默认隐藏头，头高为零；输入区仍须有效避让 |
| D6 1024 抽屉宽 | 过时 | 显式打开且等于配置，不要求旧 360 |
| D6 抽屉底不压 composer | 产品缺陷 | 新中间容器令旧手算坐标失效；TranslatePoint 获取实际视觉坐标 |
| D6 抽屉加底栏不覆盖 | 同一产品缺陷 | 保留不遮挡断言，展开底栏后仍按输入区实际坐标避让 |
| R3 长标题与详情 | 过时 | 会话头默认隐藏；显式启用后仍严格检查截断、32 DIP 详情按钮、不遮挡 |
| R3 工作区图标 | 过时 | 入口改为可配置右区开关；检查可见开关有真实 geometry |
| R3 禁用审查 Enter | 过时 | 用户明确删除审查；检查无结果且 Enter 不执行、不关闭 |
| R3 caption 32 DIP | 过时 | 全局行现为 48 DIP，按钮资产仍 46×32；检查行独占及 BorderOnly 不叠系统标题 |

构建警告：删除未使用浮窗字段；命令回调等待重命名/归档，归档异常显示失败提示。
修复首次编译缺 using Avalonia，已补齐；随后独立构建 0 警告/0 错误。
首次修复全量尚有 5 失败，追加隔离配置、真实停靠开关、等待窗口尺寸变更和图标子树定位后归零。未删除断言或放宽容差。
全量复跑：374 条 ok / 0 失败，exit 0；日志 `tmp/production-selftest.log`。
追加加强 D6：初始隐藏底栏、实际展开后再次验证，抽屉下沿从 752 变为 507，与 composer 上沿相等；17 条通过、0 失败。
解决方案构建：`dotnet build Fufucode.sln --no-restore -v quiet`，0 警告/0 错误、exit 0，日志 `tmp/production-solution-build.log`。
B 已完成。本轮所有旧尺寸/入口断言均保留对应的当前产品契约和行为验证。

### Core 补充收口

- 首次 Core 在 CLI settings 原子替换遇 UnauthorizedAccessException；原文件 Archive、非只读。独立复跑完整越过同一设置场景，未据此改 ACL 或放宽测试；后续继续留意短时文件占用。
- 第二次 Core 到末尾仍 exit 1：CLI UI 隐藏旧导航不可用，登记 R-007。修复共享动作映射和配套树深度契约，定向 13 PASS / 0 FAIL；全量再跑中。
- 设置替换失败再次在损坏文件修复场景复现，登记 R-008。增加 Windows 短时占用的有界重试；只读文件仍报错，失败不提交内存状态。新增释放锁后成功与只读拒绝两条行为测试。
- `scripts/run-core-acceptance.ps1` 仅在测试进程读取已有用户级凭据，结束恢复环境，不输出或落盘密钥。本轮 Core 全量 322 PASS / 0 FAIL / 0 SKIP，exit 0，包含 Atria 两轮真实回答与停止终态；日志 `tmp/production-core-real.log`。
- Pi.Tests 全量 21 PASS / 0 FAIL，exit 0；日志 `tmp/production-pi-tests.log`。

## 其他项

C 最终包与干净环境闭环、D 工作树分叉和 A 会话管理待完成；E/F/G 已有实现和行为证据，最终组合验收待收尾。Inno Setup 位于 Program Files (x86)，由脚本探测。

## C 打包链路阶段证据

- 脚本根目录按 PSScriptRoot 推导；ISCC 从 PATH/常见安装位置探测。package.ps1 已连接 publish、托管运行时、完整性清单、ZIP 与 Inno setup 全流程。
- 本轮产出 `out/Fufucode-1.4.0-setup.exe`，SHA256 `9D40A4077E37D6D00885848374BEBEA7C2BD7019C3F64E0FF2EC24BB56E2C62B`；该包是 B 后的阶段产物，不代表后续修改的最终包。
- integrity Verify 在 Windows PowerShell 5.1 下 16,538 文件全部一致，exit 0。
- 无人值守 Sandbox VM 安装 exit 0，安装后同数文件哈希一致。SYSTEM 非交互会话 GUI 启动失败，未通过完整安装启动卸载门禁；继续检查交互会话，不冒充通过。
- 干净 guest Restricted 执行策略确实拒绝验收脚本，因此仅对受控验收 powershell 进程使用 ExecutionPolicy Bypass，不修改机器策略。
- R-004：当前探针已有退出码传递与 process.execPath；正常运行 9 PASS/0 FAIL/1 SKIP（无另一版本），人为指定相同版本对照得到 1 FAIL 且 exit 1，失败门禁有效。
- R-005：结果写入脚本目录；bridge all 实跑 8 PASS/0 FAIL，exit 0。

## D 已完成增量

- 删除未实现 PDF 动作，不保留装饰禁用项；PDF 排版导出暂不交付，Markdown 导出保持可用。
- 完整诊断 ZIP 按现有白名单重新解析/脱敏日志，只含 system.json 和 diagnostics.jsonl。排除凭据、环境变量值、会话正文、原始配置。设置页与关于页复用真实动作。
- 深度链接 `fufucode://session/<原始 ID>?project=<绝对路径>`；保留元数据 ID 格式，拒绝相对目录、UNC、额外参数、非法 ID。安装器注册用户级协议到 App，而非 CLI 启动器；仍走现有项目信任和 SessionLease。
- 验证：solution 0 警告/0 错误；Core 全量新增 ZIP 安全断言通过（323 PASS/0 FAIL/0 SKIP，含真实模型）；App R05 当时 0 失败。链接 Unicode/空格往返和拒绝输入断言位于 Workbench 套件，已在后续默认全量 609 检查中运行通过。
- 工作树分叉仍待实现；协议安装启动要在最终包重验。

## E/G 已完成增量

- E：注册用户级 Fufucode.Desktop AUMID，调用 Windows Toast API，XML 结构化转义；每个提醒 Key 去重，失败强制回退应用内提示，即使应用内渠道原本关闭。设置/浮窗显示实际投递状态。
- 真实 `--windows-toast-probe` exit 0，通知确实进入 Windows 通知历史。未声称勿扰模式下横幅必然可见。PowerShell WinRT 的 Setting 不一定投影为属性，最终以 Show + 历史确认，不把空属性误认成关闭。
- G：模型认证独立二级窗口，列出本应用 Windows 凭据条目和环境变量来源状态、Pi auth.json 提供商名称；添加/确认移除调用实际凭据库，值不回显、不记录。不更改 Pi auth.json 或用户环境变量，不存在虚构认证 RPC。
- 验证：构建 0 警告/0 错误；Core 全量 325 PASS/0 FAIL/0 SKIP，含真实 Windows 凭据增删/枚举/解析测试，测试条目 finally 清理；Tabs 定向 0 失败，覆盖系统渠道失败时强制应用内回退。原生窗口检查受用户输入打断，未记完整认证窗口 UI 验收。
- 保守边界：Pi 自有 OAuth/API key auth.json 仅展示提供商，不擅自删除或改写；添加凭据需使用模型配置引用的变量名，新 Pi 进程生效。

## F 已完成增量（续接核对）

- 用量：三个范围均提供逐轮输入/输出/缓存读写细分；只在输入侧字段完整时计算缓存读取占比，缺字段与未完成记录显示未知，不伪造费用或额度。
- 轨迹：日志逐条 flush，运行期间即可读取；最近 500 条按请求 ID 聚合，显示时间、命令、终态、耗时、错误码，支持全部/失败/未结束过滤、刷新与详情。读取时再次白名单脱敏，跳过损坏记录，不展示原始 RPC。
- 朗读：实际 Windows SAPI 异步播放、暂停/继续/停止、语速和音量设置，状态按工作进程确认更新，只朗读助手回复。
- 新增默认全量入口遗漏修复：Workbench、Tabs、R05 不再仅由 -only 参数运行。全量不需要这些筛选参数。
- 验证：独立构建与 solution 0 警告/0 错误；默认全量 --selftest 609 条 ok、0 失败、exit 0，日志 tmp/production-f-full.log。Core 全量 327 PASS/0 FAIL/0 SKIP（含真实模型），日志 tmp/production-f-core.log。
- --speech-probe 使用静音音量验证真实 SAPI 开始、暂停、继续、停止释放进程与自然结束，5 PASS、exit 0，日志 tmp/production-speech-probe.log；未声称音频听感验收。
- 未克隆 maka/deepseek harness；采用用户建议的用量细分与可筛选调用生命周期，同时保持 Pi 实际数据边界，非其实现的复制。

## C 干净环境继续证据

- 独立 Windows Sandbox 的 WDAGUtilityAccount 下，安装、16,538 文件哈希、GUI 顶级窗口建立与正常关闭已通过；Pi 验收工装超时导致当次未走到卸载，不能据此宣告完整门禁。
- 本机 Windows PowerShell 5.1 重现输入 JSON 带 BOM，Pi 返回 Failed to parse command。设置仅该验收进程 Console.InputEncoding 为无 BOM UTF-8 后，相同正式安装布局的 get_state 830 ms 成功、exit 0；Windows 5.1 没有 StandardInputEncoding 属性，未使用不兼容设置。
- 新尝试的 Sandbox 交互登录未就绪，且用户 Escape 停止 Computer Use；该次没有安装证据。续接将以可见交互会话完成最终包的全链路，不把 SYSTEM 成功替代 GUI 验收。
# 工作树分支增量（2026-10-08）

- D 的工作树分支占位已替换为真实实现：Git 从当前 HEAD 建立 `codex/chat-*` 分支和独立工作树；Pi 1.0.0 `--fork` 复制完整持久分支，新进程使用新目录、独立会话 ID、独占 lease。原 Pi 进程继续可用。
- 最保守默认：不复制未提交改动，不继承项目资源信任；新工作树显式 DoNotTrust。失败时保留已创建工作树和可恢复会话元数据，不递归清理用户内容。
- 实施前验收：真实 Git 工作树、实际私有 Pi 历史、子会话 cwd、原文件逐字节不变、子会话二次写入拒绝、释放后恢复、审批桥接 Verified。
- 实跑 `dotnet run --project tests/Fufucode.Core.Tests --no-restore -- --worktree-only`：9 PASS、0 FAIL，exit 0；`dotnet build Fufucode.sln --no-restore -v quiet`：0 警告、0 错误。
- 测试中 Pi 恢复本身追加初始化记录，故原文件基线放在恢复完成后、分叉之前；未放宽逐字节不变检查。测试只对自建临时仓库调用 `git worktree remove`。
- 附带修复：显式会话打开时校验项目 ID，拒绝会话和指定工作目录错配；UI 原生操作仍待 A 统一验收。

## A 会话管理增量

- 四个最高分类、项目/分区二级菜单、活动优先级二级菜单、两个分叉动作和三个复制动作已接入真实数据。项目名称及新会话目录、分区改名、会话展示归属持久化；展示分组不改变已有 Pi cwd。
- 行悬停仅显示置顶/归档；操作非当前行不会错误归档当前会话。上下文菜单打开期间延迟重建，避免异步状态刷新拆掉菜单。
- 保守默认：项目目录编辑仅作用于新会话；已有会话历史及执行目录保持原身份。损坏的分组设置显示诊断并使用临时默认值，不覆盖原始内容。
- 原生鼠标已验证私有真实会话的置顶及新建分区。其余菜单通过真实 Pi、Avalonia 对话框回调与实际剪贴板验证，不能当作全部原生鼠标验收；完整视觉逐项验收仍未完成。
- 当前默认全量 App：630 ok / 0 失败 / exit 0，tmp/production-final-app.log。真实 Pi 菜单：14 PASS / 0 FAIL / exit 0，tmp/production-a-real-menus.log。Core 全量：336 PASS / 0 FAIL / 0 SKIP；Pi 全量：21 PASS / 0 FAIL。证据 tmp/production-final-core.log、tmp/production-final-pi.log。
- 更新旧 Tabs 断言：常驻“重命名此会话”按钮已不符合用户要求的仅置顶/归档悬停栏，改验四类分组和真实上下文菜单；没有放宽尺寸或通过条件。对话框保存后等待真实异步持久化完成再断言，不以 Dialog.Close 返回代替完成。
- 朗读图标改为 Speaker/Pause/Play，采用已有 Microsoft Fluent 固定提交 a563cf9166f4f91aa617557ed272612b7f0a2f72 的 MIT 几何；两项映射/资源检查纳入上述默认全量。

## 最终打包验收工装核对

- RPC 验收由“找到相同 ID”改为真实 response/get_state/success=true；stderr 持续消费、并发队列快照、正常退出码为整体硬门禁。负向工装检查 exit 0，错误响应不能制造通过。R-014 已登记。
- 当前 solution 实跑 0 警告/0 错误，tmp/production-final-build.log。A 提交 28f1266。
- 两份 Pi 0.87.1/1.0.0 历史 JSONL 删除旧机器/工作树上下文；保留 entry ID/parentId 及非 system 消息，来源注明脱敏和原 SHA，不再称脱敏文件逐字节原始捕获。结构化归一化检查通过，FakePi/负向共享断言 14 PASS/0 FAIL，tmp/production-portable-fixture.log。
- 实际 Pi 1.0.0 探针复跑 9 PASS/0 FAIL/1 SKIP，tmp/production-final-probe；缺少第二版本运行时，双向跨版本兼容仍 SKIP。进程非零退出符合存在未验证项，未将它计为全量通过。
- src/tests/scripts 定向旧机器/工作树路径核查无匹配；历史任务书的引用路径保留为问题出处，不是当前运行依赖。
- Windows Sandbox 客户端连续崩溃有 Application 1000 证据，R-015 open；不操作用户其他 VM，不做未经授权的 OS 组件修复。最终干净环境完整门禁未通过。

## 归档与发布声明复核

- R-016：旧 Compress-Archive 报符号文件共享冲突，当前无法确定占用进程。共享读取逐字节 hash 验证归档已实跑成功（15,480文件），不是删符号文件或放宽校验。负例验证源变化拒绝输出、旧ZIP保留、partial清理。
- 已修 clean-start 工装异常 finally 清理及 graceful && exit0 条件；记录与测试不把强杀当正常退出。
- 子代理只读审计结果由主 agent 独立核对：R-009 阶段状态已追加当前阻塞；R-101 四项逐条处理，仍保留版本升级数据兼容、GUI性能未验证。
- Node v24.15.0 LICENSE 获取自官方固定 tag，blob 403ea3c5717087b20e9cddaa2b4881256b6b4858，文本 SHA 4573185d56580da2b890ba34a85a409257640f1c5632eade4300137266194d18。二进制与对应本机安装 SHA 一致，未改版本。
- 许可子代理仅实现受限脚本/文档任务包；主 agent 独立测试与实际收集通过，28个运行时 NuGet/10文本、121个npm实例、36个缺文本位置。缺口清单随候选载荷分发，R-017保持open，不选择产品授权、不宣称公开发布合规。
- 新增 SoftwareRendering 仅改变自建 Sandbox 的 vGPU 设置（微软官方支持的 WARP 路径），不改变宿主或用户 VM；这是尝试可用隔离环境的配置差异，不是已证实的崩溃修复。

## 仓库外验证发现与修正

- V2 全量 package.ps1 exit0，ISCC 编译成功。但 stricter 私有 cwd 复跑发现附件自测的源码 PNG 假设：R-018。修正为产品嵌入资源后，仓库外默认全量630 ok/0失败/exit0，solution0警告/0错误；最终包正在重新生成，不沿用V2 SHA为最终交付。
- 原解压目录哈希校验15,493文件无缺失/无变更，但21个工装创建文件使 overall false：R-019。测试数据与载荷已分离，额外文件门禁不放宽。最终新布局尚待实跑。
- 本轮软件渲染 Sandbox 同样90秒交互登录超时，13:04:33 Application1000再次显示相同 XAML 异常，证据 tmp/production-final-sandbox-platform-failure.json；自建938ae92a-eaec-414f-889f-26b89b0424b6已停止，wsb list为空。
- 当前最新版私有App进程响应正常并正常关闭；Computer Use两次不能激活窗口，观察含覆盖层，未点击该覆盖层、未将截图当原生验收证明。A/G完整原生验收仍未完成。
- V3私有cwd默认全量630 ok/0失败、Pi两次get_state及正常退出通过；独立完整性15,493原文件无缺失/变更，却存在app/selftest-window.png额外文件。R-019继续open。源码截图统一移到每次唯一临时数据根/evidence，原尺寸断言保留；仓库外重跑0失败，构建0警告/0错误，见tmp/production-isolated-selftest-full.log和tmp/production-isolated-selftest-build.log。V4重新构建中，不沿用V3最终SHA。

## 最终 V4 候选与六门状态

当前任务尚未满足六门全部完成，不宣称生产级最终交付通过。

| 门 | 当前事实 | 证据 |
| --- | --- | --- |
| 1 构建 | PASS，0警告/0错误 | tmp/production-final-build-v4.log |
| 2 默认全量App | PASS，630 ok/0失败/exit0；最终ZIP在仓库外私有cwd运行127058ms | tmp/production-final-clean-start-v4.log；labs/packaging/clean-start-report.jsonl |
| 3 Core/Pi | PASS，Core336 PASS/0 FAIL/0 SKIP，Pi21 PASS/0 FAIL | tmp/production-final-core.log；tmp/production-final-pi.log |
| 4 安装包 | 本机setup编译PASS；最终V4干净安装/启动/卸载open，Sandbox宿主XAML崩溃 | tmp/production-final-package-v4.log；tmp/production-final-sandbox-platform-failure.json |
| 5 路径 | 活跃src/tests/scripts不再含旧机器/旧worktree依赖；fixture结构化脱敏并保留身份与来源hash | tmp/production-portable-fixture.log；scripts/pi-probe/fixtures/*.provenance.json及provenance.json |
| 6 记录 | 当前结果、决策、进度和失败登记已同步；历史阶段结果不覆盖最终状态 | 本节及docs/decisions.md、docs/progress.md、failure-registry.md |

- 安装器：out/Fufucode-1.4.0-setup.exe，140181856字节，SHA256 `33A19D3833603077A60A35506208FD22153E0324C97125804AB5ADEE0A501A07`。
- ZIP：out/Fufucode.zip，185797499字节，SHA256 `9F12B609A34802E7C527AB2283CC6FBBA98761FEBCFE61908E26E7B70C8BE837`。
- 最终ZIP测试根：C:\Users\mio\AppData\Local\Temp\fff-pkg-test-355cdeb0540b44a6b71d747408da50f3；package与project/logs/data/home/tmp分离。Pi ready2144ms、第二次状态64ms、正常退出82ms/exit0、残留0。
- 完整性：自测完成后15493文件missing=0/changed=0/extra=0，tmp/production-final-integrity-v4.log；R-019关闭，没有删除截图或忽略额外文件。
- 本机V4原生mock窗口3192ms创建、Responding=true、正常关闭exit0，tmp/production-final-native-start-v4.log；仅生命周期，不等同视觉/指针或性能完整验收。
- 未完成：最终包干净安装闭环；A/G全部原生交互；Fufucode旧新版数据升级兼容与完整GUI性能；PDF导出已移除未实现入口，未伪装可用；36个依赖许可文本位置缺口、产品自身授权与代码签名未完成公开分发审计。
- 阻塞边界：不更改宿主系统、不使用用户正在安装系统的VM；完整原生交互受Computer Use连续激活失败限制，不把回调测试称为指针验收。无Magpie执行connector，独立许可/升级脚本任务由当前可用继承模型子代理完成并经主agent复核。
- 后续：隔离环境恢复后用当前SHA候选运行scripts/packaging/run-sandbox.ps1，完成干净安装/启动/卸载；随后补原生交互与升级兼容。公开分发前处理许可授权/签名，不能以本机候选代替发行批准。
