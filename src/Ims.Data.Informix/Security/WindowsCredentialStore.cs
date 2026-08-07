using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Ims.Core.Connections;
using Ims.Core.Data;

namespace Ims.Data.Informix.Security;

/// <summary>
/// Stores connection passwords in Windows Credential Manager.
/// </summary>
/// <remarks>
/// <para>
/// DEC-9 and PR-1.4: credentials are held only in Windows Credential Manager, and
/// "never in a plain-text or user-readable config file". Credential Manager gives
/// per-user encryption at rest, managed by the OS, with no key for IMS to look
/// after — which is the point. IMS never sees the secret except at the moment it
/// builds a connection string.
/// </para>
/// <para>
/// The target name is derived from the connection's <see cref="ConnectionDescriptor.Id"/>
/// rather than its host or display name, so renaming or re-pointing a saved
/// connection does not orphan its credential.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "Save, Delete and Contains stay instance members so the whole credential "
                    + "surface can be substituted in tests and in the connection dialog. Making "
                    + "them static would make the vault impossible to fake.")]
public sealed class WindowsCredentialStore : ICredentialResolver
{
    /// <summary>Prefix for every entry IMS owns, so they are identifiable in the vault.</summary>
    public const string TargetPrefix = "IMS:Informix:";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>The Credential Manager target name for a connection.</summary>
    public static string TargetName(ConnectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return TargetPrefix + descriptor.Id.ToString("D");
    }

    /// <inheritdoc />
    public Task<string?> GetPasswordAsync(
        ConnectionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(TargetName(descriptor)));
    }

    /// <summary>Stores or replaces the password for a connection.</summary>
    public void Save(ConnectionDescriptor descriptor, string userName, string password)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(password);

        Write(TargetName(descriptor), userName, password);
    }

    /// <summary>Removes a stored password. Silent when there was none.</summary>
    public void Delete(ConnectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!CredDelete(TargetName(descriptor), CredTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new InvalidOperationException(
                    $"Could not delete the stored credential (Windows error {error}).");
            }
        }
    }

    /// <summary>True when a password is stored for this connection.</summary>
    public bool Contains(ConnectionDescriptor descriptor) =>
        Read(TargetName(descriptor)) is not null;

    private static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out IntPtr handle))
        {
            int error = Marshal.GetLastWin32Error();

            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Could not read the stored credential (Windows error {error}).");
        }

        try
        {
            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(handle);

            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            // The blob is UTF-16 and is NOT null-terminated, so the length is
            // authoritative. Reading past it would leak adjacent vault memory.
            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                (int)credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(handle);
        }
    }

    private static void Write(string target, string userName, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);

        // Credential Manager caps the blob at 512 bytes (256 UTF-16 characters).
        if (blob.Length > 512)
        {
            throw new ArgumentException(
                "Windows Credential Manager cannot store a password longer than 256 characters.",
                nameof(password));
        }

        IntPtr blobHandle = Marshal.AllocHGlobal(blob.Length);

        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);

            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobHandle,
                Persist = CredPersistLocalMachine,
                UserName = string.IsNullOrWhiteSpace(userName) ? target : userName,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    "Could not store the credential (Windows error "
                    + $"{Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            // Zero the copy before releasing it. The password is already in the
            // vault; leaving a plaintext copy in freed heap serves no purpose.
            for (int i = 0; i < blob.Length; i++)
            {
                Marshal.WriteByte(blobHandle, i, 0);
            }

            Marshal.FreeHGlobal(blobHandle);
            Array.Clear(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
