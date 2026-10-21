# 记录入口索引

日期：2026-10-21。目录为扁平历史存储，本页只列常用入口；完整列表用 `rg --files docs/records`。

## 长期日志

- [logs/failure-registry.md](logs/failure-registry.md)：缺陷与失败登记。
- [logs/feedback.md](logs/feedback.md)：用户反馈与复盘。
- [logs/verify-log.md](logs/verify-log.md)：历史验证记录。
- [logs/hourly-dispatch-ledger.md](logs/hourly-dispatch-ledger.md)：旧小时派工台账。
- [organization/docs-organization-2026-10-05.md](organization/docs-organization-2026-10-05.md)：2026-10-05 文档迁移记录。
- [organization/worktree-retirement-2026-10-05.md](organization/worktree-retirement-2026-10-05.md)：旧工作树收口记录。

## 近期交付与规范化

- [frontend-wiring-normalization-2026-10-20.md](frontend-wiring-normalization-2026-10-20.md)：设置台—显示分级—实际功能接线规范化。
- [cleanup-2026-10-21.md](cleanup-2026-10-21.md)：docs 与目录清理记录。
- [color-system-2026-10-21.md](color-system-2026-10-21.md)：Fluent 暗色三档底色、主题色、强调色收敛。
- [S2-settings-reorg.md](S2-settings-reorg.md)：设置三级架构历史交付。
- [MKT1-plugins-delivery.md](MKT1-plugins-delivery.md)：插件市场历史交付。
- [BRW1-browser-delivery.md](BRW1-browser-delivery.md)：浏览器设置历史交付。
- [SEEP1-reverselab-delivery.md](SEEP1-reverselab-delivery.md)：Seep 工作台历史交付。
- [CLAUDE1-backend-delivery.md](CLAUDE1-backend-delivery.md)：Claude 后端历史交付。

## 证据

- [evidence/](evidence/)：设置、移动端、远控等验收截图。
- [../research-results/](../research-results/)：外部调研结果与第三方证据。
- [../frontend-prototype/](../frontend-prototype/)：前端原型；不是产品验收。

## 使用规则

1. 先看当前权威 [docs/README.md](../README.md)，不要从单份交付记录反推当前架构。
2. 交付记录中的“待 M 接线”是当时状态；当前是否接线以代码、构建和最新测试为准。
3. 旧记录的绝对路径可能已迁移；用 `document-path-map.json` 追溯，不据此执行。
