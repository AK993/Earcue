using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Earcue.Core.Security;

/// <summary>
/// 使用 Windows DPAPI（CryptProtectData，CurrentUser 范围）加密 API Key。
/// 密文只能被同一 Windows 用户在同一台机器上解密，复制 config.json 到其他机器/账户后需要重新填写 Key。
/// </summary>
public static class ApiKeyProtector
{
    private const string Prefix = "dpapi:v1:";

    // 附加熵：让密文与"Earcue 的 LLM Key"这一用途绑定，避免被其他程序直接复用
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Earcue.LlmApiKey.v1");

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>当前系统是否支持 DPAPI 加密（仅 Windows）。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>判断配置里的值是否为本类生成的 DPAPI 密文。</summary>
    public static bool IsProtected(string? stored) =>
        !string.IsNullOrEmpty(stored) &&
        stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密 API Key。空输入返回空串；当前系统不支持时返回空串（调用方应提示）。</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || !IsSupported)
        {
            return string.Empty;
        }

        var inputBytes = Encoding.UTF8.GetBytes(plaintext);
        var inputBlob = new DataBlob
        {
            Size = inputBytes.Length,
            Data = Marshal.AllocHGlobal(inputBytes.Length)
        };
        var entropyBlob = new DataBlob
        {
            Size = Entropy.Length,
            Data = Marshal.AllocHGlobal(Entropy.Length)
        };

        try
        {
            Marshal.Copy(inputBytes, 0, inputBlob.Data, inputBytes.Length);
            Marshal.Copy(Entropy, 0, entropyBlob.Data, Entropy.Length);

            if (!CryptProtectData(
                    ref inputBlob,
                    "Earcue LLM API Key",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var outputBlob))
            {
                throw new InvalidOperationException(
                    "无法加密 API Key: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            try
            {
                var output = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, output, 0, outputBlob.Size);
                return Prefix + Convert.ToBase64String(output);
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    LocalFree(outputBlob.Data);
                }
            }
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputBlob.Data);
            }

            if (entropyBlob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlob.Data);
            }
        }
    }

    /// <summary>解密 API Key。失败（非本用户密文、密文损坏、非 Windows）时返回 null。</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !IsProtected(stored) || !IsSupported)
        {
            return null;
        }

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (cipher.Length == 0)
        {
            return null;
        }

        var inputBlob = new DataBlob
        {
            Size = cipher.Length,
            Data = Marshal.AllocHGlobal(cipher.Length)
        };
        var entropyBlob = new DataBlob
        {
            Size = Entropy.Length,
            Data = Marshal.AllocHGlobal(Entropy.Length)
        };

        try
        {
            Marshal.Copy(cipher, 0, inputBlob.Data, cipher.Length);
            Marshal.Copy(Entropy, 0, entropyBlob.Data, Entropy.Length);

            if (!CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var outputBlob))
            {
                return null;
            }

            try
            {
                var output = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, output, 0, outputBlob.Size);
                return Encoding.UTF8.GetString(output);
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    LocalFree(outputBlob.Data);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputBlob.Data);
            }

            if (entropyBlob.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyBlob.Data);
            }
        }
    }
}
