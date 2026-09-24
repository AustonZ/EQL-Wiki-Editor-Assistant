using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>
/// Stores the bot password in Windows Credential Manager, as a generic credential under
/// <see cref="DefaultTargetName"/>.
///
/// Credential Manager rather than a DPAPI-encrypted file of our own: it is the OS mechanism users already know how
/// to audit and revoke (<c>control /name Microsoft.CredentialManager</c>, or <c>cmdkey /list</c>), it is encrypted
/// per-user by the OS, and it keeps the secret out of the repo's working tree entirely — which matters here
/// because this repo has a public remote and a documented rule that credentials never live in it.
///
/// **The password is written to unmanaged memory and zeroed after use** on both paths. This is not theatre: the
/// alternative leaves the secret in a managed <c>string</c>, which is immutable, may be copied by the GC and stays
/// readable in a crash dump. The managed string still exists on the read path — .NET's API surface gives no way
/// around that without a <c>SecureString</c>, which is itself documented as no longer providing meaningful
/// protection — so this narrows the window rather than closing it, and the real protection remains the OS store.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string targetName = WindowsCredentialStore.DefaultTargetName) : ICredentialStore
{
    /// <summary>Namespaced so it is obvious in <c>cmdkey /list</c> which application owns it and what it is for.</summary>
    public const string DefaultTargetName = "EQLWikiAssistant:eqlwiki.com";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly string _targetName = targetName;

    public BotCredentials? Read()
    {
        if (!CredRead(_targetName, CredTypeGeneric, 0, out nint handle))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new InvalidOperationException($"Reading the credential '{_targetName}' failed (Win32 error {error}).");
        }

        try
        {
            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            string userName = credential.UserName is 0 ? "" : Marshal.PtrToStringUni(credential.UserName) ?? "";
            // CredentialBlob is a byte count, not a character count, and is not null-terminated.
            string password = credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) ?? "";

            return userName.Length == 0 && password.Length == 0 ? null : new BotCredentials(userName, password);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(BotCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        byte[] blob = Encoding.Unicode.GetBytes(credentials.Password);
        nint blobPointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            Array.Clear(blob);

            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = Marshal.StringToCoTaskMemUni(_targetName),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = Marshal.StringToCoTaskMemUni(credentials.UserName),
            };

            try
            {
                if (!CredWrite(ref credential, 0))
                    throw new InvalidOperationException(
                        $"Saving the credential '{_targetName}' failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
                Marshal.FreeCoTaskMem(credential.UserName);
            }
        }
        finally
        {
            // Zero before releasing, so the secret does not linger in freed heap memory.
            for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPointer, i, 0);
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    public bool Delete()
    {
        if (CredDelete(_targetName, CredTypeGeneric, 0)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new InvalidOperationException($"Deleting the credential '{_targetName}' failed (Win32 error {error}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    // DllImport rather than the newer LibraryImport: the source generator it uses emits unsafe code, and turning
    // <AllowUnsafeBlocks> on for this otherwise-portable domain assembly to gain nothing at four call sites is the
    // worse trade. Note this is plain Win32 P/Invoke, unrelated to the Capture project's COM interop, where
    // [GeneratedComInterface] genuinely is required over [ComImport] (see CLAUDE.md) — that rule is about CsWinRT
    // ComWrappers objects and does not apply here.
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out nint credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(nint buffer);
}
