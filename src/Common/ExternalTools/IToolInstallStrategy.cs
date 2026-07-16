using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Resolves a tool spec to a verified local artifact path (shared download +
/// hash-pin + cache). Implemented by <see cref="ExternalToolManager"/> and handed
/// to each strategy so every install medium reuses one fetch path.
/// </summary>
public interface IToolResolver
{
    Task<string?> ResolveAsync(ExternalToolSpec spec, CancellationToken ct = default);

    /// <summary>Fetch the remote manifest and return its latest version entry
    /// (including <see cref="ToolVersion.VersionCode"/>), without downloading the
    /// payload. Returns null when the manifest is unavailable.</summary>
    Task<ToolVersion?> GetLatestAsync(ExternalToolSpec spec, CancellationToken ct = default);
}

/// <summary>
/// Installs and runs a tool on one target medium. <see cref="ExternalToolManager"/>
/// routes by <see cref="ExternalToolSpec.Target"/>. The strategy decides when to
/// resolve - a medium can inspect its target before fetching a large payload - and
/// tracks its own tools for status and teardown.
/// </summary>
public interface IToolInstallStrategy
{
    ToolTarget Target { get; }

    Task LaunchAsync(ExternalToolSpec spec, IToolResolver resolver, CancellationToken ct);

    /// <summary>Running or Failed when this strategy owns the tool, else NotRunning.
    /// The NoDevice decision stays with the manager.</summary>
    ToolStatus GetStatus(string toolId);

    /// <summary>Stops the tool. True only when the process is confirmed gone, so a caller
    /// that then touches the device knows it is the only writer.</summary>
    bool Terminate(string toolId);

    void TerminateAll();
}
