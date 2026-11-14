using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Body of <c>POST /apps-api/proxy</c>. The host runs the actual fetch
/// after validating the URL against the widget's <c>capabilities.net.fetch</c>
/// allowlist; the response is size-capped + decoded into <see cref="AppProxyResponse"/>.
/// </summary>
public sealed class AppProxyRequest
{
    public string AppId { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Method { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    /// <summary>
    /// Hosts the caller believes the manifest allowlists. Defense in depth:
    /// the host also reads the registry's manifest and rejects when these
    /// disagree, so a tampered client can't widen its own allowlist.
    /// </summary>
    public List<string>? AllowedHosts { get; set; }
}

public sealed class AppProxyResponse
{
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string StatusText { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>
    /// JSON-decoded body when the upstream response declared
    /// <c>application/json</c>. Null otherwise (the raw text body
    /// lives in <see cref="BodyText"/>). Truncated to
    /// <see cref="AppProxyService.MaxBodyBytes"/>.
    /// </summary>
    /// <remarks>
    /// Nullable so the source-generated serializer never has to emit a
    /// default-initialised <c>JsonElement</c> (kind=Undefined), which throws.
    /// </remarks>
    public JsonElement? Body { get; set; }
    /// <summary>Raw text body when the upstream wasn't JSON.</summary>
    public string? BodyText { get; set; }
    /// <summary>
    /// Base64 of the exact upstream bytes when the content type is binary
    /// (anything that is not text, JSON, XML or script). Null for text bodies,
    /// which keep arriving in <see cref="Body"/> or <see cref="BodyText"/>.
    /// </summary>
    public string? BodyBase64 { get; set; }
    public string? Error { get; set; }
}
