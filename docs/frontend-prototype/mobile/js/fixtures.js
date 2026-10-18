/* M0 手机原型夹具数据：全部为人工合成示例，不代表真实宿主返回。
   真实接线由 D6 决定；此文件只服务界面验证。所有条目显式标注 fixture。 */
"use strict";
window.FF_FIXTURE = {
  fixture: true,
  validPairCode: "426810",
  devices: [
    { id: "dev-1", name: "我的 Pixel（本机）", fixture: true },
    { id: "dev-2", name: "旧手机", revoked: false, fixture: true },
    { id: "dev-3", name: "测试平板", revoked: true, fixture: true }
  ],
  projects: [
    { id: "p1", name: "Fufucode", path: "C:\\Projects\\Fufucode", fixture: true },
    { id: "p2", name: "demo-api", path: "D:\\work\\demo-api", fixture: true }
  ],
  sessions: [
    {
      id: "s1", projectId: "p1", title: "重构会话列表状态机",
      state: "running", unreadApproval: 1, fixture: true,
      messages: [
        { role: "user", text: "把会话列表改成按项目分组显示" },
        { role: "assistant", text: "我先看一下现有列表组件和状态来源。" },
        { role: "tool", name: "read", status: "ok",
          detail: "读取 src/Fufucode.App/Views/SessionListView.axaml.cs\n42 行，无改动" },
        { role: "tool", name: "edit", status: "pending_approval",
          approvalId: "ap-1", callId: "call-77",
          detail: "edit src/Fufucode.App/Views/SessionListView.axaml.cs\n+ GroupBy(Project)\n+ GroupHeaderTemplate" },
        { role: "assistant-streaming",
          text: "列表分组需要审批一次文件修改。批准后我继续改 ViewModel。" }
      ]
    },
    {
      id: "s2", projectId: "p1", title: "手机原型窄屏适配",
      state: "idle", unreadApproval: 0, fixture: true,
      messages: [
        { role: "user", text: "composer 在键盘弹出时被盖住" },
        { role: "assistant", text: "已改用 100dvh + visualViewport 兜底，钉在可视底部。" }
      ]
    },
    {
      id: "s3", projectId: "p2", title: "昨晚的长任务",
      state: "error", unreadApproval: 0, fixture: true,
      messages: [
        { role: "system", text: "宿主曾退出：任务终态为“中断”，不会自动重发。" }
      ]
    }
  ],
  hostStates: {
    desktopClosed: "桌面窗口已关闭，后台宿主仍在运行",
    hostExited: "后台宿主已退出，不能继续控制",
    sleep: "电脑无响应：可能已休眠"
  },
  /* 发送后演示用的流式回复（逐块追加） */
  demoReply: [
    "收到。", "我检查一下", "相关代码", "，然后给你修改方案。",
    "\n\n要点：\n1. 先看契约\n2. 再改实现\n3. 跑定向验证"
  ],
  demoToolAfterReply: {
    name: "bash", status: "ok",
    detail: "dotnet build src/Fufucode.App\n已成功生成，0 警告"
  }
};
