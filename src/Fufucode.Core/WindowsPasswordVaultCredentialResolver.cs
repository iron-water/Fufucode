using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Fufucode.Core;

/// <summary>Windows Credential Manager vault; falls back to the existing environment key path.</summary>
public sealed class WindowsPasswordVaultCredentialResolver : ICredentialStore
{
    private const uint Generic = 1, LocalMachine = 2;
    private const int NotFound = 1168;
    private readonly ICredentialResolver _fallback;

    public WindowsPasswordVaultCredentialResolver(ICredentialResolver? fallback = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _fallback = fallback ?? new EnvironmentCredentialResolver();
    }

    public async ValueTask<string?> ResolveAsync(string environmentVariable, CancellationToken cancellationToken = default)
    {
        var target = Target(environmentVariable);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return await _fallback.ResolveAsync(environmentVariable, cancellationToken);
            throw new Win32Exception(error, "Credential lookup failed.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string environmentVariable, string password)
    {
        var target = Target(environmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic, TargetName = target, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob, Persist = LocalMachine, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential save failed.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete(string environmentVariable)
    {
        if (!CredDelete(Target(environmentVariable), Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error, "Credential delete failed.");
        }
    }

    public IReadOnlyList<string> ListNames()
    {
        const string prefix = "Fufucode.CompatibleEndpoint.";
        if (!CredEnumerate(prefix + "*", 0, out var count, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return [];
            throw new Win32Exception(error, "Credential enumeration failed.");
        }
        try
        {
            var names = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(pointer, checked((int)index * IntPtr.Size));
                var credential = Marshal.PtrToStructure<NativeCredential>(item);
                if (credential.TargetName.StartsWith(prefix, StringComparison.Ordinal))
                    names.Add(credential.TargetName[prefix.Length..]);
            }
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        finally { CredFree(pointer); }
    }

    private static string Target(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Invalid credential name.", nameof(name));
        return "Fufucode.CompatibleEndpoint." + name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reserved, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);
}
