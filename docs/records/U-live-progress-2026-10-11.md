# 开发控制台核心状态与执行轨迹

入口：`Start-Process 'http://127.0.0.1:18765'`。服务未启动时执行 `pwsh -NoProfile -File C:/Projects/Fucode/Fufucode/scripts/automation-console-control.ps1 -Mode Start -Repository C:/Projects/Fucode/Fufucode`。

2026-10-11 08:17 UTC：workflow 47668、gate 37712、controls 33600、core 59668 均存活。用户 manual、调度暂停；执行模型 0。固定 M needs_attention，原进程 30560 已退出，既有日志为 1 GiB 分配失败。U1/M0 已交付待审；D4-SNAPSHOT 第二轮已接受且集成；D0/O6-RULES failed_or_missing_card。本次不恢复用户暂停，不重发不明 M 投递，不发模型请求。

所有视图常驻核心状态，区分本地核心、调度开关、M 状态、真实执行数及待审数。Windows 进程检测核对创建时间，执行者匹配出生时间，服务记录容差 5 秒。进程存活不能证明业务响应正常。

受管会话读取本任务 stdout.log，最多末尾 2 MiB；兼容 Codex item 与 OMP tool_execution，保留最后 30 条公开活动。节点显示最近活动，详情展开轨迹与已有 todo 步骤。忽略推理、工具参数、原始命令和结果正文，容忍未写完的 JSON 行。不把活动数转换为完成百分比。终末单行超出上限可能无轨迹，独立终态和交付记录仍保留。

验证：14 项 bridge 测试通过，包含 Codex/OMP 活动、失败、半行及敏感内容过滤；JS 语法通过。真实浏览器仅运行中视图仍显示核心、0 执行/2 待审；进入自动开发阶段点击 U1 展开实际 30 条轨迹。真实 M0 OMP 日志也读取到 30 条。本次只重启控制台服务，不改生产调度。截图 output/playwright/core-progress-live.png 为本地证据。

细粒度完成度需要执行者报告明确步骤及验收证据；日志展示不代表新多任务/部分交付产品协议已经集成。
