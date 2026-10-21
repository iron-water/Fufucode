# 主工作区收口记录

日期：2026-10-05。用户授权逐个审查、收编有价值内容、作废无价值内容、移除轨道 worktree，并改为主 agent 全权模式。

## 收编

- `a874e96` 已进入 main：三行所有权表、临时任务包协议、验证门前置及匹配的工作区规则。
- 保留 `.zcodeignore` 和 `docs/面向人类/Fufucode 开发技术报告（自包含文本版）.md`。技术报告是 2026-10-01 的历史快照，其中版本、统计和验收描述未在本次重新验证。
- `docs/design/frontend-design-guideline.md` 已在 main（原提交 `99af3a5`），无需重复收编。
- 原 A/B/D/E/F/G/H/I 分支均无 main 尚未包含的提交；保留历史分支，不删除提交历史。

## 作废与保留边界

- 主目录及原 F 的 `out/` 未提交变动属于生成的发布产物，本次作废，不作为新发布版本提交；主目录 tracked 产物恢复到已有提交版本。
- 空文件 `docs/面向人类/整体技术架构.md` 作废。
- 原 A 的 `a13-runtime-setup.log` 是安装成功日志，本次不收编。
- 原 G/H 的 modified 标记没有实际内容差异，重新暂存后确认没有 staged diff，不制造空提交。
- 新用户名下的副本与旧用户名登记工作区并存，多数属于较旧内容快照。未将其缺文件和较旧实现反向覆盖 main；未提交差异留为 Git stash，退出当前开发范围，避免遗漏独立内容。此保留不代表新代码验收或批准集成。

| 迁移副本 | 恢复用 stash 对象 |
|---|---|
| 6f2a（仅剩 .git 的残留） | `ae05163625dc693825bfb35d109d09e95c0839df` |
| track-a-core | `dee8e7b7d577bb84432fbed66ca43a92a88d344c` |
| track-b-bridge | `c027b2bd6b0a5bcbe3c43ae1cc42e8a4dcae3c33` |
| track-d-app | `bbcab29455c8abcfc7b14849c1f6598432a36e3c` |
| track-f-packaging | `b6cadb8c8856b1a5051e4129580dbbf6cf5462ef` |
| track-g-assets | `eacd83daa344173baffee1121085b5d929ac9ef0` |
| track-h-study | `fa886d913d7b4955189274988a73a781f0af65e0` |

8570 内容与其提交无差异，未产生 stash。日志、临时资料及可能的参考资料先复制到 `C:\Projects\Fufucode\tmp\worktree-retirement-2026-10-05`（本地忽略目录）。可重新获取的依赖和构建缓存不收编。

## 清理与验证

- 使用 `git worktree remove` 移除八个原轨道、两个游离工作区及六个迁移副本。副本共享登记失效后先临时重新登记，再保存差异和移除。
- 五个 WorkBuddy 登记由 Git 标为 gitdir 指向不存在位置，使用 `git worktree prune` 清理登记；其旧路径读取遭权限拒绝，未直接删除或声称审查了那些目录内容。对应分支已在 main 中。
- 最终 `git worktree list` 只列 `C:/Projects/Fufucode [main]`；`git branch --no-merged main` 无输出。
- 原 I 路径仍有 Git 移除后遗留的 `runtime/pi` 目录，已无 `.git`，不再是工作区；未绕过权限或直接递归删除此残留。
- 文档差异检查通过，主目录 Git 状态干净。没有改产品代码，没有执行产品构建、运行或 UI 验收。
