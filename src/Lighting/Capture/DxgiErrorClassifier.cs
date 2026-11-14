namespace Nexus.Service.Lighting.Capture;

public enum DxgiFailureAction
{
    /// <summary>No new frame yet; not an error.</summary>
    Timeout,
    /// <summary>The duplication is invalid but the device and output are still good.</summary>
    RecreateDuplication,
    /// <summary>The D3D device itself is gone or unusable.</summary>
    RebuildDevice,
}

/// <summary>
/// Maps the HRESULTs of AcquireNextFrame and DuplicateOutput to the cheapest
/// recovery that works, and names them for the helper log.
/// </summary>
public static class DxgiErrorClassifier
{
    public const int InvalidCall = unchecked((int)0x887A0001);
    public const int Unsupported = unchecked((int)0x887A0004);
    public const int DeviceRemoved = unchecked((int)0x887A0005);
    public const int DeviceHung = unchecked((int)0x887A0006);
    public const int DeviceReset = unchecked((int)0x887A0007);
    public const int DriverInternalError = unchecked((int)0x887A0020);
    public const int NotCurrentlyAvailable = unchecked((int)0x887A0022);
    public const int ModeChangeInProgress = unchecked((int)0x887A0025);
    public const int AccessLost = unchecked((int)0x887A0026);
    public const int WaitTimeout = unchecked((int)0x887A0027);
    public const int SessionDisconnected = unchecked((int)0x887A0028);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int InvalidArg = unchecked((int)0x80070057);
    public const int OutOfMemory = unchecked((int)0x8007000E);
    public const int Fail = unchecked((int)0x80004005);

    public static DxgiFailureAction Classify(int hresult) => hresult switch
    {
        WaitTimeout => DxgiFailureAction.Timeout,
        DeviceRemoved or DeviceReset or DeviceHung or DriverInternalError or OutOfMemory => DxgiFailureAction.RebuildDevice,
        _ => DxgiFailureAction.RecreateDuplication,
    };

    public static string Name(int hresult) => hresult switch
    {
        0 => "S_OK",
        InvalidCall => "DXGI_ERROR_INVALID_CALL",
        Unsupported => "DXGI_ERROR_UNSUPPORTED",
        DeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
        DeviceHung => "DXGI_ERROR_DEVICE_HUNG",
        DeviceReset => "DXGI_ERROR_DEVICE_RESET",
        DriverInternalError => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        NotCurrentlyAvailable => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        ModeChangeInProgress => "DXGI_ERROR_MODE_CHANGE_IN_PROGRESS",
        AccessLost => "DXGI_ERROR_ACCESS_LOST",
        WaitTimeout => "DXGI_ERROR_WAIT_TIMEOUT",
        SessionDisconnected => "DXGI_ERROR_SESSION_DISCONNECTED",
        AccessDenied => "E_ACCESSDENIED",
        InvalidArg => "E_INVALIDARG",
        OutOfMemory => "E_OUTOFMEMORY",
        Fail => "E_FAIL",
        _ => "unknown",
    };

    public static string Describe(int hresult) => $"0x{hresult:X8} {Name(hresult)}";
}
