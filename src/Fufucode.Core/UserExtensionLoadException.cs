namespace Fufucode.Core;

public enum UserExtensionLoadFailureKind { MissingPath, PiStartupFailure }

/// <summary>Failure to prepare or load an explicitly selected user extension.</summary>
public sealed class UserExtensionLoadException : InvalidOperationException
{
    public UserExtensionLoadException(string extensionPath)
        : base($"Selected extension path does not exist: {extensionPath}.")
    {
        ExtensionPath = extensionPath;
        Kind = UserExtensionLoadFailureKind.MissingPath;
    }

    public UserExtensionLoadException(string extensionPath, Exception cause)
        : base($"Pi failed to load selected extension: {extensionPath}.", cause)
    {
        ExtensionPath = extensionPath;
        Kind = UserExtensionLoadFailureKind.PiStartupFailure;
    }

    public string ExtensionPath { get; }
    public UserExtensionLoadFailureKind Kind { get; }
}
