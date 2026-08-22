using System.Runtime.InteropServices;
using System.Text;

namespace FocusListener.App;

internal static class WindowsCredentialStore
{
    private const string Target = "FocusListener/GeminiApiKey";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public static string? ReadApiKey()
    {
        if (!CredRead(Target, GenericCredential, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void WriteApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var secret = Encoding.Unicode.GetBytes(apiKey.Trim());
        if (secret.Length > 2560)
        {
            throw new ArgumentOutOfRangeException(nameof(apiKey), "API key is too long for Windows Credential Manager.");
        }

        var pinned = GCHandle.Alloc(secret, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = Target,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = pinned.AddrOfPinnedObject(),
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            pinned.Free();
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static void DeleteApiKey()
    {
        if (!CredDelete(Target, GenericCredential, 0))
        {
            const int notFound = 1168;
            var error = Marshal.GetLastWin32Error();
            if (error != notFound)
            {
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
