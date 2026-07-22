using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace StepWind.Core.Updates;

/// <summary>The outcome of an Authenticode trust check on a file.</summary>
public enum SignatureTrust
{
    /// <summary>The file carries no Authenticode signature at all.</summary>
    NoSignature,

    /// <summary>The file is signed, but the signature/chain is not trusted (bad hash, untrusted root, expired, revoked, explicitly distrusted).</summary>
    Untrusted,

    /// <summary>The file is signed and the signature verifies to a trusted root.</summary>
    Trusted,

    /// <summary>The trust check could not be performed (unexpected error).</summary>
    Error,
}

/// <summary>
/// Thin wrapper over Windows' native Authenticode verification (<c>WinVerifyTrust</c>). This is
/// the real root of trust for the auto-updater: a SHA-256 checksum published in the SAME GitHub
/// release an attacker would tamper with is not an independent signal, but a code signature that
/// chains to a trusted CA (and, once we pin it, to StepWind's own certificate) is. The SYSTEM
/// service refuses to launch any downloaded installer that isn't <see cref="SignatureTrust.Trusted"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2 — the standard "is this file's Authenticode signature valid?" action.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    // Don't reach out to the network for revocation while an update is being decided; a trusted
    // chain with an unreachable CRL must not silently downgrade to "untrusted" on an offline box.
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;

    /// <summary>
    /// Verifies the Authenticode signature of <paramref name="filePath"/>. Returns
    /// <see cref="SignatureTrust.Trusted"/> only when Windows confirms a valid signature chaining
    /// to a trusted root. Never throws.
    /// </summary>
    public static SignatureTrust VerifyFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return SignatureTrust.Error;
        }

        IntPtr pFile = IntPtr.Zero;
        IntPtr pData = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = Marshal.StringToCoTaskMemUni(filePath),
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE,
            };
            pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, false);

            Guid action = GenericVerifyV2;
            uint result = WinVerifyTrust(IntPtr.Zero, ref action, pData);

            // Always close the state handle WinVerifyTrust opened, regardless of the result.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, false);
            WinVerifyTrust(IntPtr.Zero, ref action, pData);

            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);

            return result switch
            {
                0 => SignatureTrust.Trusted,
                TRUST_E_NOSIGNATURE => SignatureTrust.NoSignature,
                TRUST_E_PROVIDER_UNKNOWN => SignatureTrust.Error,
                _ => SignatureTrust.Untrusted, // signed but not trusted: bad hash, untrusted root, expired, revoked, distrusted
            };
        }
        catch
        {
            return SignatureTrust.Error;
        }
        finally
        {
            if (pFile != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pFile);
            }

            if (pData != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pData);
            }
        }
    }

    /// <summary>
    /// The signer certificate's thumbprint (uppercase hex), or null if the file isn't signed or
    /// the certificate can't be read. Used to PIN updates to StepWind's own certificate once
    /// releases are signed — a valid signature by some other trusted publisher is not enough.
    /// </summary>
    public static string? SignerThumbprint(string filePath)
    {
        try
        {
            // SYSLIB0057 flags cert *loading* constructors, but CreateFromSignedFile is the only
            // API that extracts the Authenticode signer certificate from a PE file — there is no
            // non-obsolete replacement for that. GetCertHashString() returns the SHA-1 thumbprint
            // as uppercase hex (identical to X509Certificate2.Thumbprint) with no cert re-loading.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return cert.GetCertHashString();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);
}
