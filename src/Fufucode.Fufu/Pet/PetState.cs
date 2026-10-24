namespace Fufucode.Fufu.Pet;

/// <summary>桌宠行为状态（任务书 §四）：确定性行为集合，由事件/规则驱动，不依赖 LLM。</summary>
public enum PetState
{
    Idle,       // 待机
    Walk,       // 移动中
    Rest,       // 休息
    Interact,   // 被点击交互
    Thinking,   // 思考中（模型推理）
    Working,    // 任务执行中
    Success,    // 任务完成（短暂反馈）
    Error,      // 任务失败（短暂反馈）
    Attention,  // 需要用户关注
}
