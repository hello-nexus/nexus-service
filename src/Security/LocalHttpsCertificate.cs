using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Service.Security;

public static class LocalHttpsCertificate
{
    /// <summary>
    /// SHA-256 of the SubjectPublicKeyInfo (DER-encoded), base64url with no padding.
    /// Embedded in the panel-phone pair QR so the iOS companion app can pin the
    /// expected cert before its first TLS handshake.
    /// </summary>
    public static string ComputeSpkiBase64Url(X509Certificate2 cert)
    {
        using var key = cert.GetRSAPublicKey() ?? (AsymmetricAlgorithm?)cert.GetECDsaPublicKey();
        if (key is null) return string.Empty;
        var spki = key.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static X509Certificate2 LoadOrCreate()
    {
        var path = ResolveCertificatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            return X509CertificateLoader.LoadPkcs12(existing, password: null, GetStorageFlags());
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=Nexus Local",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            san.AddDnsName(Environment.MachineName);
        }
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (var ip in GetLocalIpv4Addresses())
        {
            san.AddIpAddress(ip);
        }

        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        var pfx = cert.Export(X509ContentType.Pfx);
        // The PKCS#12 has no password, so its private key must never be readable
        // by anyone else - restrict before the bytes land, not after.
        Persistence.NexusDataPaths.CreateRestricted(path);
        File.WriteAllBytes(path, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, GetStorageFlags());
    }

    private static X509KeyStorageFlags GetStorageFlags()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return X509KeyStorageFlags.UserKeySet |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.Exportable;
        }

        // .NET 10 on macOS rejects EphemeralKeySet for PKCS12 import.
        // DefaultKeySet works on both macOS and Linux (in-process key on
        // macOS, OpenSSL-backed on Linux); no keychain persistence is needed.
        return X509KeyStorageFlags.DefaultKeySet;
    }

    private static string ResolveCertificatePath()
    {
        // Root system daemon: one machine-scope cert, no session needed.
        if (Persistence.NexusDataPaths.SystemDaemonRoot is { } daemonRoot)
            return Path.Combine(daemonRoot, "nexus-local-https.pfx");

        string root;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope cert: LocalSystem service owns it, shared across
            // all users on the box. The phone trusts one cert per host, not
            // one per user.
            root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(root, "Nexus", "nexus-local-https.pfx");
    }

    private static IEnumerable<IPAddress> GetLocalIpv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip) &&
                    !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    yield return ip;
                }
            }
        }
    }
}
