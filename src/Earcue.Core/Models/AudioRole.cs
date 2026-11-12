namespace Earcue.Core.Models;

public enum AudioRole
{
    /// <summary>
    /// 会议对方（系统环回音频）
    /// </summary>
    Remote = 0,

    /// <summary>
    /// 我方（麦克风音频，仅作上下文参考，不触发问题回答）
    /// </summary>
    Local = 1
}
