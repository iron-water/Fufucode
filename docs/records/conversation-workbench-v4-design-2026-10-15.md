# CONV-AUTO-v4研究与设计交接

- 状态：design_ready；产品实施与原生验收未执行。
- 角色：提问线程研究设计，主agent（管理）负责实施、复核、共享接线与提交。
- 主控：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`，host=`local`。
- 工作目录：`C:\Projects\Fucode\Fufucode`；未创建额外worktree。
- 观察起点HEAD：`81c18c24143115e9b8559934f8c5b01dee7ac5d5`；交接前实际HEAD：`b38996d1993e7b9f9ac25229d973d9463bc3eda9`。主控同期仍写入，实施前复核源码，不把本记录当冻结候选。
- 提交：本轮文档与样稿未提交，留给持有主线写入权的M白名单审核提交；未暂存或修改其他人的改动。

## 产物

1. `docs/research/conversation-workbench-gap-2026-10-15.md`：14项静态差距与Codex官方资料链接。
2. `docs/design/conversation-workbench-v4.md`：新默认流程、程序与M职责、消息/交付/恢复/迁移。
3. `docs/design/conversation-workbench-v4-ui.md`：对话总览、执行线程、成果验收、活动记录与控制语义。
4. `docs/tasks/conversation-workbench-v4.md`：两个端到端实施包和14场景验收。
5. `docs/frontend-prototype/automation/conversation-v4/`：可审阅示例，运行边界与检查记录见其README。
6. 更新AI_DEVELOPMENT_PLAN、automation-workbench、conversation-driven-development、parallel-development-control、autonomous-development-control五个既有入口；明确v4取代旧人工默认及旧界面入口。

## 验证

已执行：源码与官方文档核对；JS语法检查；独立浏览器13项交互断言；两个视口截图审阅；重新加载控制台0错误0警告；`git diff --check`无输出、退出0。本轮未调用付费模型，不需要模型测试预算。

未执行：产品编译、全量自测、真实自主执行、原生UI、安装包、后台/登录启动。仅文档和独立示例改动，不将本轮浏览器检查算作产品验收。

## 主控下一步

先对账当前在途任务与源码变化，读取v4任务，再落实AUTO-LOOP纵向闭环；投影明确后UI可独立并行。保留现有状态核心，不另造数据库/第二调度器；本轮用户明确要求新项目默认自动，已有暂停/人工/在途身份不变。主控须按实际实现更新差距，不能只确认收到或宣称原型即完成。

接线需求：生产Core/Host/适配器、原生页面与设置、迁移和后台均待M组织实施。未变更本机权限、设备、登录启动或外部两小时监督任务；临时静态服务与独立浏览器验证会话在交接前关闭，未触碰其他会话。

回传：应主动发送至上列真实主控线程；是否发送成功以本轮消息工具结果为准，不能由文件存在推断已接收或已处理。M收到后需核对本卡与产物，再统一审核、记录与整合。

stoppedWriting=true
