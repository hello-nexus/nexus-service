using System;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Windows-only NVAPI access via nvapi64.dll. Interface ids, struct layouts,
/// and the I2C parameters mirror LibreHardwareMonitor's Interop/NvApi.cs
/// (Initialize's GetDelegate calls, NvI2CInfo) and Hardware/Gpu/NvidiaGpu.cs
/// TryReadAstral12VHPwrPinSensors, so this reads the identical protocol LHM
/// already ships, just without its hardcoded AstralSubSystemIds allow-list.
/// </summary>
internal sealed class AstralNvApiClient : IAstralNvApiClient
{
    private const string DllName = "nvapi64.dll";

    [DllImport(DllName, EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl, PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface(uint interfaceId);

    // NvAPI_QueryInterface ids, from LibreHardwareMonitor Interop/NvApi.cs Initialize().
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetPciIdentifiers = 0x2DDFB66E;
    private const uint IdI2CReadEx = 0x4D7B0709;

    private const int MaxPhysicalGpus = 64;

    // Astral's onboard ITE IT8915FN power-monitor IC. AstralI2CDevAddress is
    // the chip's I2C address shifted into the form NvI2CInfo.I2CDevAddress
    // expects; AstralRegisterAddress is where its power/current registers
    // begin. Same address NvidiaGpu.cs TryReadAstral12VHPwrPinSensors uses,
    // independently confirmed by Timic3/astral-power-monitoring's SMBus dump.
    private const byte AstralI2CDevAddress = 0x2B << 1;
    private const byte AstralRegisterAddress = 0x80;
    private const uint I2CSpeedDeprecated = 0xFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvPhysicalGpuHandle
    {
#pragma warning disable CS0169, CS0649
        private readonly IntPtr _ptr;
#pragma warning restore CS0169, CS0649
    }

    private enum NvStatus
    {
        Ok = 0,
    }

    private enum NvI2CSpeed : uint
    {
        Speed100Khz = 4,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NvI2CInfo
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDDCPort;
        public byte I2CDevAddress;
        public IntPtr I2CRegAddress;
        public uint RegAddrSize;
        public IntPtr Data;
        public uint Size;
        public uint I2CSpeed;
        public NvI2CSpeed I2CSpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus EnumPhysicalGpusDelegate([Out] NvPhysicalGpuHandle[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus GetPciIdentifiersDelegate(
        NvPhysicalGpuHandle handle, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus I2CReadExDelegate(NvPhysicalGpuHandle handle, ref NvI2CInfo info, ref uint readData);

    private static EnumPhysicalGpusDelegate? _enumPhysicalGpus;
    private static GetPciIdentifiersDelegate? _getPciIdentifiers;
    private static I2CReadExDelegate? _i2cReadEx;

    private static bool _bound;
    private static bool _available;
    private static bool _loggedUnavailable;

    private static bool EnsureBound()
    {
        if (_bound)
        {
            return _available;
        }
        _bound = true;

        try
        {
            var initPtr = QueryInterface(IdInitialize);
            if (initPtr == IntPtr.Zero)
            {
                LogUnavailable("nvapi64.dll has no NvAPI_Initialize entry point");
                return false;
            }

            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);
            if (initialize() != NvStatus.Ok)
            {
                LogUnavailable("NvAPI_Initialize failed");
                return false;
            }

            _enumPhysicalGpus = Bind<EnumPhysicalGpusDelegate>(IdEnumPhysicalGpus);
            _getPciIdentifiers = Bind<GetPciIdentifiersDelegate>(IdGetPciIdentifiers);
            _i2cReadEx = Bind<I2CReadExDelegate>(IdI2CReadEx);

            _available = _enumPhysicalGpus is not null && _getPciIdentifiers is not null && _i2cReadEx is not null;
            if (!_available)
            {
                LogUnavailable("required NVAPI entry points missing");
            }
            return _available;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LogUnavailable($"nvapi64.dll unavailable: {e.GetType().Name}");
            return false;
        }
    }

    private static T? Bind<T>(uint interfaceId) where T : class
    {
        var ptr = QueryInterface(interfaceId);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static void LogUnavailable(string reason)
    {
        if (_loggedUnavailable)
        {
            return;
        }
        _loggedUnavailable = true;
        ServiceLog.Info($"[astral] NVAPI supplement disabled: {reason}");
    }

    private static bool TryGetHandle(int adapterIndex, out NvPhysicalGpuHandle handle)
    {
        handle = default;
        if (!EnsureBound())
        {
            return false;
        }

        var handles = new NvPhysicalGpuHandle[MaxPhysicalGpus];
        if (_enumPhysicalGpus!(handles, out var count) != NvStatus.Ok)
        {
            return false;
        }
        if (adapterIndex < 0 || adapterIndex >= count)
        {
            return false;
        }

        handle = handles[adapterIndex];
        return true;
    }

    public bool TryGetPciSubsystemId(int adapterIndex, out uint subSystemId)
    {
        subSystemId = 0;
        if (!TryGetHandle(adapterIndex, out var handle))
        {
            return false;
        }
        return _getPciIdentifiers!(handle, out _, out subSystemId, out _, out _) == NvStatus.Ok;
    }

    private readonly byte[] _telemetryBuffer = new byte[AstralTelemetryParser.BlockSize];
    private readonly byte[] _gSensorBuffer = new byte[AstralGSensorParser.BlockSize];
    private readonly byte[] _telemetryRegister = { AstralRegisterAddress };
    private readonly byte[] _gSensorRegister = { AstralGSensorParser.RegisterAddress };

    public bool TryReadAstralBlock(int adapterIndex, out byte[] block) =>
        TryReadRegisters(adapterIndex, AstralRegisterAddress, AstralTelemetryParser.BlockSize, out block);

    public bool TryReadGSensorBlock(int adapterIndex, out byte[] block) =>
        TryReadRegisters(adapterIndex, AstralGSensorParser.RegisterAddress, AstralGSensorParser.BlockSize, out block);

    private bool TryReadRegisters(int adapterIndex, byte register, int size, out byte[] block)
    {
        block = Array.Empty<byte>();
        if (!TryGetHandle(adapterIndex, out var handle))
        {
            return false;
        }

        // Reused: the owner reads one block at a time and finishes with it before the next read.
        var buffer = register == AstralRegisterAddress ? _telemetryBuffer : _gSensorBuffer;
        var regAddress = register == AstralRegisterAddress ? _telemetryRegister : _gSensorRegister;
        NvStatus status;

        unsafe
        {
            fixed (byte* pData = buffer)
            fixed (byte* pRegAddr = regAddress)
            {
                var info = new NvI2CInfo
                {
                    Version = MakeVersion<NvI2CInfo>(3),
                    DisplayMask = 0,
                    IsDDCPort = 0,
                    I2CDevAddress = AstralI2CDevAddress,
                    I2CRegAddress = (IntPtr)pRegAddr,
                    RegAddrSize = 1,
                    Data = (IntPtr)pData,
                    Size = (uint)size,
                    I2CSpeed = I2CSpeedDeprecated,
                    I2CSpeedKhz = NvI2CSpeed.Speed100Khz,
                    PortId = 1,
                    IsPortIdSet = 1,
                };

                uint readData = 0;
                status = _i2cReadEx!(handle, ref info, ref readData);
            }
        }

        if (status != NvStatus.Ok)
        {
            return false;
        }

        block = buffer;
        return true;
    }

    private static uint MakeVersion<T>(int version) => (uint)(Marshal.SizeOf<T>() | (version << 16));
}
