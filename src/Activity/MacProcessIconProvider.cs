using System;
using System.IO;

namespace Nexus.Service.Activity;

/// <summary>
/// macOS IProcessIconProvider: resolves a process exe to its owning .app
/// bundle and hands extraction to the shared MacAppIconExtractor.
/// </summary>
public sealed class MacProcessIconProvider : IProcessIconProvider
{
    private readonly MacAppIconExtractor _extractor;

    public MacProcessIconProvider(MacAppIconExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>Null only when the extractor timed out (transient, not cached
    /// by the route); empty bytes when extraction ran and found no icon.</summary>
    public byte[]? GetIcon(string exePath)
    {
        // A .app directory is accepted as well: MacScreenTimeProvider reports the bundle path as the focused app's ExePath.
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(exePath) || !(File.Exists(exePath) || Directory.Exists(exePath)))
        {
            return Array.Empty<byte>();
        }
        return _extractor.ExtractPng(ResolveIconTargetPath(exePath));
    }

    /// <summary>Outermost .app ancestor of the exe path (helper-bundle
    /// processes like browser renderers then surface the product's icon,
    /// not a generic executable); the exe path itself when no bundle owns
    /// it.</summary>
    internal static string ResolveIconTargetPath(string exePath)
    {
        var segments = exePath.Split('/');
        var prefixLength = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            prefixLength += segments[i].Length + (i > 0 ? 1 : 0);
            if (segments[i].Length > ".app".Length &&
                segments[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return exePath[..prefixLength];
            }
        }
        return exePath;
    }
}
