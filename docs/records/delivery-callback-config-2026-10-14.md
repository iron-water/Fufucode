# 交付回传与两小时补漏配置记录

日期：2026-10-14。直接用户授权见docs/tasks/delivery-callback-protocol.md。

- 原automation ID fufucode复用；应用automation_update返回Updated、ACTIVE。
- 名称改为Fufucode 交付回传与两小时补漏；目标仍为主agent（管理）01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07，每两小时一次。
- 主agent (提问) 01a12596-9397-7132-b8c9-095b38fa726f未创建定时任务。
- 保留既有通知偏好、唯一主控、两执行槽、验收及成本纪律；没有更改产品源码、工作树或在途执行者模型，没有新增调度器。
- 新派/正常续接需加入带直接人类授权来源的主动回传要求；当前worker不打断。
- 已从automation.toml读回确认heartbeat/ACTIVE/两小时间隔/主控目标ID匹配，automations目录仍仅原一条fufucode。应用send_message_to_thread返回目标主控threadId，已投递本次直接用户授权、协议入口、新派/正常续接规则与白名单收编要求；此返回只证明投递，不证明主控已处理。
- 真实worker完成回传、去重/忙时队列/失败补扫尚未实跑，不能标完整验收通过；主控后续记录首次实际回传。不启动额外模型探针。

本提问线程不修改main产品实现、不擅自提交正在由主控持有的共享索引。此配置相关文档由主控在现有main写入租约下白名单提交，避免竞争提交。

M接管核查（第60轮）：使用read_thread直接读人类turn01a13994-7315-7ec1-808a-808325eb87ee内userMessage01a13994-73c3-74b3-be7e-34171e21f403，与本协议引用原文一致。M另读现automation.toml的kind/name/status/rrule/target_thread_id确认原heartbeat/ACTIVE/两小时/目标管理线程不变；没有再次创建或更新另一个调度器。原Aero与已续接native继续，不注入新要求；正常后续派工/续接带回传授权。配置协调消息已处理不等于第一项真实worker交付回传已验证。

M第62/63轮实测：E7-NATIVE R2一次delivery.received已主动到达，M先待收，随后核实际completed/idle、clean、HEAD d3a34da。旧源卡为部分原生证据，决定rework/not_integrated，不自动接受。首次真实发送已验证；重复通知去重、失败补扫、所有未来worker发送不因这一例而升级。Aero正常返工prompt已实际带完整直接人类授权与绑定，不注入运行正文。
