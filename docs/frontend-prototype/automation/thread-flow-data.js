'use strict';
// 示例数据。线程、任务、任务内交付部分是三种不同身份。
window.threadFlow = {
  width: 2110, height: 1610,
  groups: [
    {id:'main',title:'主控层',subtitle:'固定总控 M · 审核、接线与门禁都在同一线程',x:20,y:30,w:370,h:1090},
    {id:'first',title:'首轮开发层',subtitle:'依赖与共享文件占用决定开工，不代表一次启动所有线程',x:420,y:30,w:1670,h:1090},
    {id:'codex',parent:'first',title:'Codex 执行线程',subtitle:'核心 / 原生 / 共享接线',x:440,y:100,w:790,h:995},
    {id:'omp',parent:'first',title:'OMP 执行线程',subtitle:'受限模块 / 原型 / 配置',x:1250,y:100,w:820,h:995},
    {id:'later',title:'后续阶段',subtitle:'G3 通过后才解锁 · 图上预先展示，未自动创建会话',x:420,y:1150,w:1670,h:430}
  ],
  threads: [
    {id:'M',title:'M · 项目总控',harness:'Codex',session:'demo-fixed-manager',group:'main',x:40,y:120,
      tasks:[['G0','范围 / 所有权 / 验收','integrated'],['PLAN','拆分与批准任务图','integrated'],['REVIEW','逐份独立审核','waiting'],['WIRE','共享接线 / 主线收编','waiting'],['G1','双后端原生验收门','blocked'],['G2','后台生命周期验收门','blocked'],['QV','定向 / 原生验证','running'],['R','全量 / 迁移 / 打包 / 干净安装','waiting'],['G3','首轮稳定版本验收门','blocked']]},
    {id:'core',title:'核心与后台宿主',harness:'Codex',session:'demo-codex-core',group:'codex',x:460,y:170,
      tasks:[['D1-P','双后端协议','delivered',[['message','消息格式','integrated'],['stop','停止事件','delivered']]],['D1-A','Pi / OMP 适配','running'],['D1-I','身份迁移与能力表','accepted'],['D1-R','审批 / 保存会话恢复','waiting'],['D5-H','唯一后台宿主 / IPC','blocked'],['D5-E','命令去重 / 快照 / 事件补齐','blocked'],['U1','持久任务 / 部分交付契约','running'],['U2','跨 harness 执行与原会话恢复','blocked']]},
    {id:'desktop',title:'桌面基础与 Git',harness:'Codex',session:'demo-codex-desktop',group:'codex',x:860,y:170,
      tasks:[['D2-I','终端输入 / 粘贴 / IME','running'],['D2-T','选择 / 滚动 / TUI','waiting'],['D3-P','浏览器标签 / profile','waiting'],['D3-D','下载 / 权限 / 原生宿主','waiting'],['D4-S','只读差异快照','integrated'],['D4-O','Git 操作 / 占用保护','waiting'],['D0','发布环境 / 旧 UI 文案','waiting']]},
    {id:'management',title:'管理面板与资料',harness:'OMP',session:'demo-omp-management',group:'omp',x:1270,y:170,
      tasks:[['O1','快捷键编辑与冲突','delivered',[['list','动作列表','integrated'],['conflicts','冲突校验','delivered']]],['O2-P','存储计量 / 清理预览','running'],['O2-S','保护范围 / 保留 / 取消','waiting'],['O3-L','资料收藏 / 来源 / 版本','running'],['O3-I','全文索引 / 导入导出','waiting'],['O3-R','引用 / 副本 / 实际路径','waiting']]},
    {id:'modules',title:'审查、插件与调度',harness:'OMP',session:'demo-omp-modules',group:'omp',x:1690,y:170,
      tasks:[['O4-S','审查范围 / 行号快照','blocked'],['O4-L','过时定位 / 后端接线','blocked'],['O5','插件生命周期 / 更新回退','blocked'],['O6-F','一次性 / 周期 / 漏跑','waiting'],['O6-H','定时后台接线','blocked'],['O7','图像供应商 / 历史 / 收藏','waiting']]},
    {id:'native',title:'原生接线与移动控制',harness:'Codex',session:'demo-codex-native',group:'codex',x:460,y:680,
      tasks:[['S2-T','主题机制 / 共享控件','waiting'],['S2-S','选定生产皮肤接入','blocked'],['D6-P','手机配对 / 审批有效性','blocked'],['D6-R','流式重连 / 真机验证','blocked'],['U3','动画流程图功能 tab','running'],['V','焦点 / 输入 / DPI / 返回路径','waiting']]},
    {id:'design',title:'视觉与手机原型',harness:'OMP',session:'demo-omp-design',group:'omp',x:1270,y:680,
      tasks:[['S1','Fluent / Metro / Material 样稿','integrated'],['M0','WorkBuddy 观察 / 手机原型','waiting'],['U0','线程层级图 / 动画 / 交互','running']]},
    {id:'advanced',title:'高级桌面与远控',harness:'Codex',session:'尚未创建 · 规划线程',group:'later',x:460,y:1230,
      tasks:[['D8','浏览器 Agent / CDP / 标注','blocked'],['D9','手机外网 / 撤销 / 推送','blocked'],['S3','Luna / Aero / 合成可行性','blocked']]},
    {id:'pr',title:'PR 精确审查',harness:'OMP',session:'尚未创建 · 规划线程',group:'later',x:1270,y:1230,
      tasks:[['O8-H','精确 PR HEAD / 行号','blocked'],['O8-C','授权评论 / 过时检查','blocked']]}
  ],
  edges: [
    ['scope-core','M','G0','core','D1-P'],['scope-management','M','G0','management','O1'],['scope-design','M','G0','design','S1'],
    ['core-review','core','D1-P','M','REVIEW','delivery'],['review-core','M','REVIEW','core','D1-P','rework'],
    ['management-review','management','O1','M','REVIEW','delivery'],['review-management','M','REVIEW','management','O1','rework'],
    ['g1-host','M','G1','core','D5-H'],['g1-terminal','M','G1','desktop','D2-I'],['g1-browser','M','G1','desktop','D3-P'],['g1-modules','M','G1','modules','O5'],
    ['git-review','desktop','D4-S','modules','O4-S'],['library-image','management','O3-I','modules','O7'],['shortcuts-terminal','management','O1','desktop','D2-I'],
    ['skin-native','design','S1','native','S2-S'],['mobile-native','design','M0','native','D6-P'],
    ['g2-mobile','M','G2','native','D6-P'],['g2-schedule','M','G2','modules','O6-H'],['host-u2','core','D5-H','core','U2'],
    ['u1-u2','core','U1','core','U2'],['u2-tab','core','U2','native','U3'],
    ['desktop-integrate','desktop','D4-O','M','WIRE','delivery'],['modules-integrate','modules','O4-L','M','WIRE','delivery'],
    ['g3-advanced','M','G3','advanced','D8'],['g3-pr','M','G3','pr','O8-H'],
    ['protocol-g1','core','D1-P','M','G1'],['adapter-g1','core','D1-A','M','G1'],['identity-g1','core','D1-I','M','G1'],['restore-g1','core','D1-R','M','G1'],
    ['host-g2','core','D5-H','M','G2'],['events-g2','core','D5-E','M','G2'],
    ['terminal-release','desktop','D2-T','M','R'],['browser-release','desktop','D3-D','M','R'],['git-release','desktop','D4-O','M','R'],['environment-release','desktop','D0','M','R'],
    ['shortcut-release','management','O1','M','R'],['storage-release','management','O2-S','M','R'],['library-release','management','O3-R','M','R'],
    ['review-release','modules','O4-L','M','R'],['plugin-release','modules','O5','M','R'],['schedule-release','modules','O6-H','M','R'],['image-release','modules','O7','M','R'],
    ['skin-release','native','S2-S','M','R'],['mobile-release','native','D6-R','M','R'],['native-release','native','V','M','R'],
    ['release-g3','M','R','M','G3'],['qv-g1','M','QV','M','G1'],['qv-g2','M','QV','M','G2'],['qv-release','M','QV','M','R'],
    ['g3-remote','M','G3','advanced','D9'],['g3-skins','M','G3','advanced','S3']
  ]
};
