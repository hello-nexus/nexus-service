using System;
using System.Collections.Concurrent;
using System.IO;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Confirms that a process whose name matched a catalog entry is really that
/// vendor's app. Entries that carry <see cref="ConflictAppDefinition.Signers"/>
/// are matched on the executable's identity as well as its name, because a
/// bare name collides across vendors. Entries without Signers pass untouched.
///
/// An unreadable path, or an identity that names no listed signer, rejects the
/// match: ending or warning about the wrong app costs more than a missed warning.
/// </summary>
public sealed class ConflictSignerGate
{
    /// <summary>What an executable says about who made it. Signer is the Authenticode signer certificate's full subject, empty when the file is unsigned.</summary>
    public readonly record struct Identity(string? Signer, string? Company);

    private const int MaxCached = 512;

    private readonly Func<int, string?> _pathOf;
    private readonly Func<string, Identity> _identityOf;
    private readonly ConcurrentDictionary<(int Pid, string Path), Identity> _cache = new();

    public ConflictSignerGate(Func<int, string?> pathOf, Func<string, Identity> identityOf)
    {
        _pathOf = pathOf;
        _identityOf = identityOf;
    }

    /// <summary>The gate the service uses: image path from the limited-query right, identity from the file on disk.</summary>
    public static ConflictSignerGate Default { get; } = new(ReadPath, ReadIdentity);

    /// <summary>Whether the running process <paramref name="pid"/> counts as <paramref name="def"/>. Always true for an entry with no Signers.</summary>
    public bool Matches(ConflictAppDefinition def, int pid, string? knownPath = null)
    {
        if (def.Signers.Length == 0)
        {
            return true;
        }
        string? path;
        try
        {
            path = string.IsNullOrWhiteSpace(knownPath) ? _pathOf(pid) : knownPath;
        }
        catch
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        return Decide(def.Signers, IdentityFor(pid, path));
    }

    /// <summary>Same decision for an executable that is not (yet) a running process, such as a Run value's target.</summary>
    public bool MatchesPath(ConflictAppDefinition def, string path)
        => def.Signers.Length == 0 || (!string.IsNullOrWhiteSpace(path) && Decide(def.Signers, IdentityFor(0, path)));

    /// <summary>The signer decides when the file is signed; the company name is consulted only for an unsigned file.</summary>
    internal static bool Decide(string[] signers, Identity identity)
    {
        var field = !string.IsNullOrEmpty(identity.Signer) ? identity.Signer : identity.Company;
        if (string.IsNullOrEmpty(field))
        {
            return false;
        }
        foreach (var signer in signers)
        {
            if (signer.Length > 0 && field.Contains(signer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private Identity IdentityFor(int pid, string path)
    {
        var key = (pid, path);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        Identity identity;
        try
        {
            identity = _identityOf(path);
        }
        catch
        {
            return default;
        }
        // An empty identity may be a transient read failure, so it is retried next scan.
        if (identity.Signer is null && identity.Company is null)
        {
            return identity;
        }
        if (_cache.Count >= MaxCached)
        {
            _cache.Clear();
        }
        _cache[key] = identity;
        return identity;
    }

    private static string? ReadPath(int pid)
    {
        try
        {
            return Nexus.Service.Lifecycle.OwnNexusExe.ImagePathOf(pid);
        }
        catch
        {
            return null;
        }
    }

    private static Identity ReadIdentity(string path)
    {
#if WINDOWS
        if (!File.Exists(path))
        {
            return default;
        }
        string signer = "";
        try
        {
            using var cert = Nexus.Service.Platform.Windows.AuthenticodeSigner.TryGetSignerCertificate(path);
            if (cert is not null)
            {
                signer = cert.Subject;
            }
        }
        catch
        {
        }
        if (signer.Length > 0)
        {
            return new Identity(signer, null);
        }
        try
        {
            return new Identity("", System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName ?? "");
        }
        catch
        {
            return default;
        }
#else
        return default;
#endif
    }
}
