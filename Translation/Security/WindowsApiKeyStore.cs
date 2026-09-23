using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ScreenTranslator.Translation.Security;

public sealed class WindowsApiKeyStore : IApiKeyStore
{
    private const string DefaultTargetName = "ScreenTranslator/DeepSeek";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private readonly string _targetName;
    public WindowsApiKeyStore() : this(DefaultTargetName) { }
    internal WindowsApiKeyStore(string targetName) => _targetName = string.IsNullOrWhiteSpace(targetName) ? throw new ArgumentException("凭据名称不能为空。", nameof(targetName)) : targetName;

    public bool HasKey
    {
        get
        {
            var value = Read();
            return !string.IsNullOrWhiteSpace(value);
        }
    }

    public string? Read()
    {
        if (!CredRead(_targetName, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API Key 不能为空。", nameof(apiKey));
        var normalized = apiKey.Trim();
        var bytes = Encoding.Unicode.GetBytes(normalized);
        if (bytes.Length > 5120) throw new ArgumentException("API Key 长度异常。", nameof(apiKey));
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = _targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安全保存 API Key。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            unsafe { new Span<byte>((void*)blob, bytes.Length).Clear(); }
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (!CredDelete(_targetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "无法删除 API Key。");
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
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref Credential userCredential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out nint credentialPtr);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
