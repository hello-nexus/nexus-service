namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Which class of adapter a context should bind to.</summary>
internal enum GpuAdapterKind
{
    /// <summary>No request: the backend picks as it always has.</summary>
    Auto,
    Integrated,
    Dedicated,

    /// <summary>The Microsoft software rasterizer.</summary>
    Warp,
}

/// <summary>
/// Process-wide adapter request for a context about to be created. Only the
/// lighting bench sets it (in its throwaway child process); the service leaves it
/// at <see cref="GpuAdapterKind.Auto"/>. A backend that can target an adapter
/// reads it when it creates its display or device: <see cref="Luid"/> names the
/// adapter as "high:low", the form <c>GpuAdapterLuids.Adapter.Luid</c> uses, and
/// is null for <see cref="GpuAdapterKind.Warp"/> and <see cref="GpuAdapterKind.Auto"/>.
/// A backend that cannot honor it (WGL steers by the per-exe GpuPreference
/// instead) ignores it.
/// </summary>
internal static class GpuAdapterRequest
{
    private static volatile GpuAdapterKind s_kind;
    private static volatile string? s_luid;

    public static GpuAdapterKind Kind => s_kind;

    public static string? Luid => s_luid;

    public static void Set(GpuAdapterKind kind, string? luid)
    {
        s_luid = luid;
        s_kind = kind;
    }
}
