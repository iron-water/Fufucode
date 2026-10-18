# P1 桌面主路径收口

推荐模型 group/auto-qfmodel；难度 L3。已有模块的行为验收和局部修复，避免重写。机制模型试跑显式 devin/swe-2。

起点：main 5dce651519f5197d84ca242ca026c063c6d1d9ba 后的任务书提交。在新 Codex 隔离工作树执行，不同步或集成 main。

先读 AGENTS、AI_DEVELOPMENT_PLAN、development-topology-v2、execution-contract、session-handoff-protocol、D1/O1/O2/O3 集成记录。

内部顺序：V1 Pi/OMP 原生拒绝无写入、停止无后续执行、再次发送、取消选择、缺失后端返回；V2 快捷键编辑实际生效、存储仅临时数据清理保护、资料导入搜索引用复制及焦点/缩放；V3 仅 V1/V2 适用门满足后提出并验证资料到正确聊天会话接线。无法原生操作须记录缺失工具及未验证项，继续独立定向修复，不用 headless 冒充原生。保留设置页与辅助栏基线。

允许路径：src/Fufucode.App/Features/Shortcuts/**、Storage/**、Library/**；对应独立 tests 和 SelfTest 中本模块已有测试文件；docs/records/P1-desktop.md。MainWindow*/Integration/公共动作/Core/Settings/Styles 由主控唯一持有：需要改变时在任务记录提出具体接线补丁及接口，不擅自写。可用临时日志，不提交凭据。

排除：新皮肤、重写模块、生产调度、后台宿主、安装、main 进度/决策、全局 failure registry、push/清理。缺陷登记在任务记录，主控统一登记。保护继承内容。

交付：可验证增量提交；完整交付卡写记录和终答，列工作树/起点/HEAD/全部提交/文件、实际命令结果与证据、未验证项、共享接线需求、停止写入状态。明确 V1/V2/V3 各自事实，模块待接线有效，不自称整项验收。完成后停写，等原会话返工，不开下一任务。
