namespace Fufucode.Core.Automation.Hosting;

/// <summary>
/// Line protocol between the one background host and its clients. One JSON object per line, so a
/// client can be closed, crashed or replaced at any time without touching the host or a run in
/// flight. Event continuation uses the U1 event journal cursors; there is no second event store.
///
/// client → host: HELLO, PING, SNAPSHOT, EVENTS, SUBSCRIBE, COMMAND, STOP, BYE
/// host → client: same type in reply, plus SUBSCRIBED and pushed EVENT frames
/// </summary>
public static class HostProtocol
{
    public const int Version = 1;

    public const string Hello = "HELLO";
    public const string Ping = "PING";
    public const string Pong = "PONG";
    public const string Snapshot = "SNAPSHOT";
    public const string Events = "EVENTS";
    public const string Subscribe = "SUBSCRIBE";
    public const string Subscribed = "SUBSCRIBED";
    public const string Event = "EVENT";
    public const string Command = "COMMAND";
    public const string Queued = "QUEUED";
    public const string Ack = "ACK";
    public const string Stop = "STOP";
    public const string Stopping = "STOPPING";
    public const string Bye = "BYE";
    public const string Closed = "CLOSED";
    public const string Error = "ERROR";
}
