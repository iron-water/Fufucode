namespace Fufucode.App.Abstractions;

/// <summary>Immutable engine identity of a chat, independent of its selected model.</summary>
public interface IBackendChatSession
{
    string BackendId { get; }
    string BackendName { get; }
}
