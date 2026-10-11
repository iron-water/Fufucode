namespace Fufucode.Core;

/// <summary>Optional command completion: accepted prompt, no run events, and get_state confirms idle.</summary>
public interface IPromptHandledSession
{
    event Action? PromptHandled;
}
