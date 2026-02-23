using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using sharpclaw.UI;

namespace sharpclaw.Core;

/// <summary>
/// 跨平台凭据存储：将加密密钥存入 OS 凭据管理器。
/// Windows: Credential Manager (P/Invoke)
/// macOS: Keychain (security CLI)
/// Linux: libsecret (secret-tool CLI)
/// </summary>
public static class KeyStore
{
    private const string ServiceName = "sharpclaw";
    private const string AccountName = "config-key";

    public static byte[] GetOrCreateKey()
    {
        var key = TryGetKey();
        if (key is not null) return key;

        key = RandomNumberGenerator.GetBytes(32);
        StoreKey(key);
        AppLogger.Log("[KeyStore] 已生成并存储加密密钥");
        return key;
    }

    private static byte[]? TryGetKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsCredential.Read(ServiceName, AccountName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return MacKeychain.Read(ServiceName, AccountName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxSecretTool.Read(ServiceName, AccountName);

        throw new PlatformNotSupportedException("不支持的操作系统");
    }

    private static void StoreKey(byte[] key)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            WindowsCredential.Write(ServiceName, AccountName, key);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            MacKeychain.Write(ServiceName, AccountName, key);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            LinuxSecretTool.Write(ServiceName, AccountName, key);
        else
            throw new PlatformNotSupportedException("不支持的操作系统");
    }

    #region Windows Credential Manager

    private static class WindowsCredential
    {
        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;

        public static byte[]? Read(string service, string account)
        {
            var target = $"{service}:{account}";
            if (!CredReadW(target, CredTypeGeneric, 0, out var credPtr))
                return null;

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                if (cred.CredentialBlobSize <= 0) return null;
                var blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
                return blob;
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        public static void Write(string service, string account, byte[] data)
        {
            var target = $"{service}:{account}";
            var blob = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, blob, data.Length);
                var cred = new CREDENTIAL
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    UserName = account,
                    CredentialBlob = blob,
                    CredentialBlobSize = data.Length,
                    Persist = CredPersistLocalMachine,
                };
                if (!CredWriteW(ref cred, 0))
                    throw new InvalidOperationException(
                        $"写入 Windows 凭据失败: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }
    }

    #endregion

    #region macOS Keychain

    private static class MacKeychain
    {
        public static byte[]? Read(string service, string account)
        {
            var (exitCode, output) = RunProcess("security",
                $"find-generic-password -s \"{service}\" -a \"{account}\" -w");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;
            try { return Convert.FromBase64String(output.Trim()); }
            catch { return null; }
        }

        public static void Write(string service, string account, byte[] data)
        {
            var b64 = Convert.ToBase64String(data);
            // 先尝试删除旧条目
            RunProcess("security", $"delete-generic-password -s \"{service}\" -a \"{account}\"");
            var (exitCode, _) = RunProcess("security",
                $"add-generic-password -s \"{service}\" -a \"{account}\" -w \"{b64}\"");
            if (exitCode != 0)
                throw new InvalidOperationException("写入 macOS Keychain 失败");
        }
    }

    #endregion

    #region Linux libsecret

    private static class LinuxSecretTool
    {
        public static byte[]? Read(string service, string account)
        {
            var (exitCode, output) = RunProcess("secret-tool",
                $"lookup service \"{service}\" account \"{account}\"");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;
            try { return Convert.FromBase64String(output.Trim()); }
            catch { return null; }
        }

        public static void Write(string service, string account, byte[] data)
        {
            var b64 = Convert.ToBase64String(data);
            var psi = new ProcessStartInfo("secret-tool",
                $"store --label=\"{service}\" service \"{service}\" account \"{account}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(b64);
            proc.StandardInput.Close();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException("写入 Linux secret-tool 失败");
        }
    }

    #endregion

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return (proc.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }
}
