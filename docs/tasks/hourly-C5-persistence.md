# C5-PERSIST：原生控制客户端的本地草稿与请求记录模块

任务：C5-PERSIST｜难度：L3｜推荐主模型：group/auto-qfmodel｜理由：边界明确的本地版本化文件模块，但需原子保存、身份隔离、竞争与损坏保真。备选group/auto-glm-5-3-flash仅原模型不可用时协调；不自动换模型。不是测试lead；机制模型请求仅显式devin/swe-2，本任务无需云请求。

由M授权新Codex Desktop隔离工作树，冻结产品源a35f36045fc68088e81dda7c67b227b6551a98a4（包含C5第二增量）；开工起点必须包含本任务书。读AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、hourly-C5-native；核实实际cwd/branch或detached/HEAD/继承状态。只写实际工作树，不回主区，不自行同步/整合未来main。

## 完整范围

交付供M公共接线的纯本地持久化模块：一个用户明确提供的数据根，按项目key、数据库规范路径、M提供的数据库稳定身份三个字段绑定scope；本模块不解析/迁移Core库、不判断新路径等于旧权威。保存/重新打开草稿，以及发送前的不可变请求、未知结果和已观察回执。未知请求只列出供人核对，不自动执行、重写revision或后台重发。必须在交付中给出精确API/状态及M接线示例。

仅允许修改：

- 新增 `src/Fufucode.App/Features/Automation/Persistence/**`，不可改现有AutomationControlViewModel/View/其partial。
- 新增 `tests/Fufucode.App.Automation.Persistence.Tests/**` 独立无云console验证工程，可引用App或直接链接本模块源；不给全量runner或sln接线。
- `docs/records/C5-persistence.md`（本任务记录）。

不改Core/Host/组合根/MainWindow/Settings/Styles/全局SelfTestRunner、进度/decisions/failure-registry或旧队列。M本轮同期开发表单公共入口，严禁越界。模块待M接线是有效交付，不为“可用”改共享文件。发现相关缺陷写本任务记录并发协调卡。

## 数据与行为

scope是完整绝对数据库路径（不打开数据库）+非空projectKey+非空authorityIdentity；文件名由规范化scope的哈希产生，不能从用户key拼路径。Windows相同路径大小写/斜线应归同scope，项目/稳定库身份不同必须隔离；跨平台语义不要擅用Windows比较。每份文件内部再次记录并核验scope与formatVersion，避免改名/换库误恢复。

草稿包含待办ID/kind、完整context/evidenceToken及其摘要、reason/evidence/response、选中候选ID；不把新版本自动套用旧草稿。请求包含不可变commandId/type、精确payloadJson及SHA256（原bytes）、创建时间；类型限定当前control apply/review/reconsider/repair/integration/knowledge/relation/restructure，没有runtime/shell执行。Store只做存储，绝不调用Core或启动任何harness。原ID同输入重读幂等，原ID不同输入拒绝；状态pending_unknown→receipt_observed，回执内容不可变，不把错误文字当决定或成功。

每scope文件维护递增本地generation，写入须预期generation匹配（包括两个store实例）；并发冲突明示，不能last-writer-wins静默丢稿。原子替换同目录临时文件，Flush到盘后发布；读取共享和锁行为明确，失败保留原文件。保守容量/UTF8大小上限；拒绝重复对象ID/请求ID、未知格式/非法JSON/损坏hash、scope错配/非法状态/超限，原字节不重置或覆盖，返回可诊断结果或明确异常。未来格式不得自动降级。不得删除用户草稿/来源；本模块只操作其自己固定目录/文件。

## 验收（真实临时文件，无模型）

两个实例/重启读同scope草稿与精确不可变请求；不同项目/同路径不同authority隔离；Windows合法路径等价；路径逃逸无机会；重复ID输入保真；同generation竞争仅一方成功且无丢失；原子失败（真正的文件锁/权限/可注入I/O边界）保原字节；读者持句柄按声明行为；非法/未来格式、重复JSON键、摘要不符/非法状态/超限保原字节拒绝；unknown不触发任何执行；回执重读不会追加新请求。测试不得仅镜像实现或把不支持项算PASS。

dotnet build独立工程和App零警告错误；执行独立工程实际断言，全量App可运行则运行并记录，未验证原生/UI接线如实待M。引用App时核查WebView2传递WPF/WinForms引用的真实剥离（参考Storage.Tests），不降警告/关闭审计制造绿。

必要增量提交，只暂存允许文件，保护继承状态。完成主动完整交付卡写记录并终答展示（路径/branch/起点/全部SHA/改动/实际命令结果/证据/未验/接口），最后停写；不push、不reset/清树、不派下一任务。本模块不等于跨重启UI功能已接线或生产验收。
