using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Update;

/// <summary>
/// Integrity checks for a staged installer. SHA-256 is always enforced.
/// Authenticode chain + signer-thumbprint verification is built but gated
/// behind a compile-time constant; flip it to true when the Nexus signing
/// certificate is live.
/// </summary>
public static class UpdateIntegrity
{
    // SHA-256 is always enforced. Authenticode chain + durable-identity
    // verification is built but stays disabled until a signed release has been
    // verified end to end on Windows; flip AuthenticodeEnforced to true then. The
    // non-enforced path still logs whether the durable-identity EKU is present, so
    // a signed build can be confirmed before enforcement is turned on.
    // Trust boundary: SHA-256 from SHA256SUMS (author-published) detects
    // tampering of the staged file; it does not prove the release author's
    // identity. The durable-identity EKU check provides that.

#if WINDOWS
    // static readonly, not const: a const false makes the `if (AuthenticodeEnforced)`
    // branch compile-time unreachable (CS0162). This is a runtime deployment toggle.
    private static readonly bool AuthenticodeEnforced = false;

    // Artifact Signing renews the signing cert daily (72h validity), so its
    // thumbprint and Subject DN are not durable. Microsoft embeds a per-identity
    // "durable identity" EKU (prefix 1.3.6.1.4.1.311.97.) unique to a subscriber's
    // identity validation; that is the stable value to pin. This is the EKU on the
    // nexus-public certificate profile (American Future Technology Corp).
    private const string DurableIdentityEku = "1.3.6.1.4.1.311.97.136769232.76527832.760955542.540107317";
#endif

    private const int BufferSize = 81920;

    /// <summary>
    /// Verifies the staged installer at <paramref name="path"/>. Always checks
    /// SHA-256. When Authenticode enforcement is enabled (Windows only),
    /// also verifies the Authenticode chain and the durable-identity EKU.
    /// Throws <see cref="InvalidDataException"/> on any failure.
    /// </summary>
    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha256))
        {
            throw new InvalidDataException("Cannot verify: SHA-256 is required.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Staged installer not found at {path}.");
        }

        var actualSha = await ComputeSha256Async(path, ct);
        if (!string.Equals(actualSha, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch on {Path.GetFileName(path)}: got {actualSha}, expected {expectedSha256.ToLowerInvariant()}.");
        }

#if WINDOWS
        if (AuthenticodeEnforced)
        {
            VerifyAuthenticode(path);
        }
        else
        {
            // Log Authenticode state without blocking when not enforced.
            try
            {
                using var cert = GetSignerCertificate(path);
                if (cert is null)
                {
                    Console.Error.WriteLine("[update-integrity] Authenticode: (not signed)");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[update-integrity] Authenticode thumbprint {cert.Thumbprint}; durable-identity EKU present: {HasDurableIdentityEku(cert)}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update-integrity] Authenticode read failed (not enforced): {ex.Message}");
            }
        }
#endif
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyAuthenticode(string path)
    {
        using var cert = GetSignerCertificate(path)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not Authenticode-signed.");

        if (!HasDurableIdentityEku(cert))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} signer lacks the Nexus durable-identity EKU {DurableIdentityEku}.");
        }

        VerifyTrustChain(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static System.Security.Cryptography.X509Certificates.X509Certificate2? GetSignerCertificate(string path)
        => Nexus.Service.Platform.Windows.AuthenticodeSigner.TryGetSignerCertificate(path);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool HasDurableIdentityEku(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext is System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                {
                    if (string.Equals(oid.Value, DurableIdentityEku, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>WinVerifyTrust: signature hash + chain to a trusted root, plus
    /// whole-chain revocation unless <paramref name="revocation"/> is false
    /// (an offline box fails the revocation fetch, not the signature).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void VerifyTrustChain(string path, bool revocation = true)
    {
        var actionGuid = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        unsafe
        {
            var fileInfo = new WinVerifyTrustFileInfo();
            fixed (char* p = path)
            {
                fileInfo.cbStruct = (uint)sizeof(WinVerifyTrustFileInfo);
                fileInfo.pcwszFilePath = p;
                fileInfo.hFile = IntPtr.Zero;
                fileInfo.pgKnownSubject = IntPtr.Zero;

                var trustData = new WinTrustData();
                trustData.cbStruct = (uint)sizeof(WinTrustData);
                trustData.dwUIChoice = 2; // WTD_UI_NONE
                trustData.fdwRevocationChecks = revocation ? 1u : 0u; // WTD_REVOKE_WHOLECHAIN / WTD_REVOKE_NONE
                trustData.dwUnionChoice = 1; // WTD_CHOICE_FILE
                trustData.pFile = &fileInfo;
                trustData.dwStateAction = 0; // WTD_STATEACTION_IGNORE

                var result = WinVerifyTrust(IntPtr.Zero, ref actionGuid, ref trustData);
                if (result != 0)
                {
                    throw new InvalidDataException($"WinVerifyTrust returned 0x{result:X8}.");
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionID,
        ref WinTrustData pWVTData);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private unsafe struct WinVerifyTrustFileInfo
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WinVerifyTrustFileInfo* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
#endif
}
