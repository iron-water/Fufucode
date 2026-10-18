namespace Fufucode.Core;

public enum PiExtensionUiResponseKind { Value, Confirmation, Cancelled }

public sealed record PiExtensionUiResponse
{
    private PiExtensionUiResponse(PiExtensionUiResponseKind kind, string? value, bool? confirmed)
    {
        Kind = kind;
        Value = value;
        Confirmed = confirmed;
    }

    public PiExtensionUiResponseKind Kind { get; }
    public string? Value { get; }
    public bool? Confirmed { get; }

    public static PiExtensionUiResponse WithValue(string value) => new(PiExtensionUiResponseKind.Value,
        value ?? throw new ArgumentNullException(nameof(value)), null);
    public static PiExtensionUiResponse Confirm(bool confirmed) => new(PiExtensionUiResponseKind.Confirmation, null, confirmed);
    public static PiExtensionUiResponse Cancel() => new(PiExtensionUiResponseKind.Cancelled, null, null);
}
