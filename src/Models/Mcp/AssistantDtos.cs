using System.Collections.Generic;

namespace Nexus.Service.Models.Mcp;

/// <summary>Wire shape for GET /ai/assistant/status.</summary>
public sealed class AssistantStatusResponse
{
    /// <summary>"notInstalled" | "downloading" | "installed" | "starting" | "running" | "error".</summary>
    public string RuntimeState { get; set; } = "";
    public bool SystemOllamaDetected { get; set; }
    /// <summary>AiIntegration.UseSystemOllama: whether an Ollama already on the default port may be adopted.</summary>
    public bool UseSystemOllama { get; set; }
    public AssistantDownloadProgressDto? DownloadProgress { get; set; }
    public List<AssistantInstalledModelDto> InstalledModels { get; set; } = new();
    public string ActiveModel { get; set; } = "";
    public List<AssistantCatalogModelDto> Catalog { get; set; } = new();
    public AssistantBusyDto? Busy { get; set; }
    public string? LastError { get; set; }
}

public sealed class AssistantDownloadProgressDto
{
    public long Received { get; set; }
    public long Total { get; set; }
}

public sealed class AssistantInstalledModelDto
{
    public string Id { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class AssistantCatalogModelDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public long DownloadBytes { get; set; }
    public string RamHint { get; set; } = "";
    public bool Recommended { get; set; }
}

/// <summary>"installingRuntime" | "removingRuntime" | "pullingModel" | "removingModel".</summary>
public sealed class AssistantBusyDto
{
    public string Kind { get; set; } = "";
    public string? Model { get; set; }
}

public sealed class AssistantModelPullRequest
{
    public string Model { get; set; } = "";
}

public sealed class AssistantModelRemoveRequest
{
    public string Model { get; set; } = "";
}

public sealed class AssistantModelSelectRequest
{
    public string Model { get; set; } = "";
}

public sealed class AssistantUseSystemRequest
{
    public bool Enabled { get; set; }
}

public sealed class AssistantQueryRequest
{
    public string Prompt { get; set; } = "";
}

/// <summary>Wire shape for a successful POST /ai/assistant/query.</summary>
public sealed class AssistantQueryResponse
{
    public string Answer { get; set; } = "";
    public List<AssistantToolRunDto> ToolsRun { get; set; } = new();
}

public sealed class AssistantToolRunDto
{
    public string Name { get; set; } = "";
    public string Args { get; set; } = "";
    public bool Ok { get; set; }
}

/// <summary>WS frame for the "aiAssistant" multiplex topic: runtime + download
/// progress and, while a pull is active, its own progress. Subscribers use this
/// for live progress bars; GET /ai/assistant/status remains the canonical
/// resource for everything else.</summary>
public sealed class AssistantProgressFrame
{
    public long Revision { get; set; }
    public string RuntimeState { get; set; } = "";
    public AssistantDownloadProgressDto? DownloadProgress { get; set; }
    public AssistantPullProgressDto? Pull { get; set; }
}

public sealed class AssistantPullProgressDto
{
    public string Model { get; set; } = "";
    public string Status { get; set; } = "";
    public long? Received { get; set; }
    public long? Total { get; set; }
}
