using System;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Widgets;

/// <summary>
/// Resolved view of one installed widget. The <see cref="RootPath"/> is the
/// directory under which <c>manifest.json</c> and <c>index.html</c> live.
/// </summary>
public sealed class AppEntry
{
    public required string Id { get; init; }
    public required string RootPath { get; init; }
    public required AppManifest Manifest { get; init; }
    public required AppInstallPaths.Source Source { get; init; }
    /// <summary>
    /// UTC install timestamp for user-source entries, derived from the app
    /// directory's creation time. Null for bundled and dev sources.
    /// </summary>
    public DateTimeOffset? InstalledAt { get; init; }
}
