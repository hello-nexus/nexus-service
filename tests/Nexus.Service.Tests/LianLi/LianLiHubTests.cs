using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Tests.LianLi;

/// <summary>
/// Records every SetFeature and Write call so transport routing can be asserted.
/// </summary>
internal sealed class HubTransportSpy : IHidDevice
{
    public enum CallKind { Feature, Write, OutputReport }

    public readonly record struct Call(CallKind Kind, byte[] Bytes)
    {
        public bool IsSetFeature => Kind == CallKind.Feature;
    }
    public List<Call> Calls { get; } = new();

    /// <summary>When true every write is rejected, as a hub that enumerated but is not answering does.</summary>
    public bool RejectWrites { get; set; }

    /// <summary>Accept this many writes, then reject the rest; negative disables staging. Reaches guards past the first.</summary>
    public int RejectAfter { get; set; } = -1;

    private bool Accept() => !RejectWrites && (RejectAfter < 0 || Calls.Count <= RejectAfter);

    public int VendorId => LianLiProtocol.VendorId;
    public int ProductId => LianLiProtocol.ProductId;
    public string Path => "spy";
    public string? Serial => null;
    public int UsagePage => LianLiProtocol.VendorUsagePage;
    public int Usage => LianLiProtocol.VendorUsage;

    public bool SetFeature(ReadOnlySpan<byte> report) { Calls.Add(new Call(CallKind.Feature, report.ToArray())); return Accept(); }
    public bool Write(ReadOnlySpan<byte> report) { Calls.Add(new Call(CallKind.Write, report.ToArray())); return Accept(); }
    public bool GetFeature(Span<byte> buffer) => false;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool SetOutputReport(ReadOnlySpan<byte> report) { Calls.Add(new Call(CallKind.OutputReport, report.ToArray())); return Accept(); }
    public int Read(Span<byte> buffer, int timeoutMs) => 0;
    public void Dispose() { }
}

public class LianLiHubTests
{
    private static LianLiFanProfile SlInfinityProfile()
    {
        LianLiFanProfiles.TryGet(0xA102, out var p);
        return p;
    }

    private static LianLiFanProfile SlProfile()
    {
        LianLiFanProfiles.TryGet(0xA100, out var p);
        return p;
    }

    // ── Transport routing ──

    [Fact]
    public void SetSpeed_sends_both_commands_via_SetFeature()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile());

        hub.SetSpeed(0, 50);

        // manual-mode write + set-speed write, both feature reports
        Assert.Equal(2, spy.Calls.Count);
        Assert.True(spy.Calls[0].IsSetFeature, "manual-mode must be SetFeature");
        Assert.True(spy.Calls[1].IsSetFeature, "set-speed must be SetFeature");
    }

    [Fact]
    public void SetSpeed_manual_mode_uses_profile_register()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile()); // ManualRegister=98=0x62

        hub.SetSpeed(0, 50);

        // byte[2] of manual-mode command is the family's ManualRegister
        Assert.Equal(0x62, spy.Calls[0].Bytes[2]);
        // byte[3] is the channel selector: 0x10 << ch (ch=0 -> 0x10)
        Assert.Equal(0x10, spy.Calls[0].Bytes[3]);
    }

    [Fact]
    public void SetSpeed_set_speed_applies_floored_duty()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile()); // floored

        hub.SetSpeed(0, 0); // duty=0 -> floored to byte 1

        Assert.Equal(1, spy.Calls[1].Bytes[3]);
    }

    [Fact]
    public void SetDuty_sends_set_speed_via_SetFeature()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile());

        hub.SetDuty(0, 50);

        Assert.Single(spy.Calls);
        Assert.True(spy.Calls[0].IsSetFeature, "set-speed must be SetFeature");
        Assert.Equal(50, spy.Calls[0].Bytes[3]);
    }

    [Fact]
    public void SetReleaseMode_sends_via_SetFeature_with_profile_register()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlProfile()); // ManualRegister=49=0x31

        hub.SetReleaseMode(0);

        Assert.Single(spy.Calls);
        Assert.True(spy.Calls[0].IsSetFeature, "release-mode must be SetFeature");
        Assert.Equal(0x31, spy.Calls[0].Bytes[2]); // Sl family register
        Assert.Equal(0x11, spy.Calls[0].Bytes[3]); // sync bit set: 0x11<<0
    }

    [Fact]
    public void SendColorData_sends_via_control_pipe_output_report()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile());

        hub.SendColorData(0, ReadOnlySpan<byte>.Empty);

        Assert.Single(spy.Calls);
        // Interrupt-OUT is accepted by the stack but never reaches the LEDs on
        // fw 1.4 (camera-verified 2026-08-27); L-Connect uses the control pipe.
        Assert.Equal(HubTransportSpy.CallKind.OutputReport, spy.Calls[0].Kind);
    }

    [Fact]
    public void SendColorData_sl_v1_sends_via_interrupt_out()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlProfile());

        hub.SendColorData(2, new byte[] { 10, 20, 30 });

        var call = Assert.Single(spy.Calls);
        Assert.Equal(HubTransportSpy.CallKind.Write, call.Kind);
        Assert.Equal(LianLiProtocol.OutputReportSize, call.Bytes.Length);
        Assert.Equal(0xE0, call.Bytes[0]);
        Assert.Equal(0x32, call.Bytes[1]); // 0x30 | channel
        Assert.Equal(new byte[] { 10, 30, 20 }, call.Bytes[2..5]); // R, B, G on the wire
    }

    [Fact]
    public void SendStartAction_sl_v1_sends_packed_quantity_feature_report()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlProfile());

        hub.SendStartAction(2, 3);

        var call = Assert.Single(spy.Calls);
        Assert.Equal(HubTransportSpy.CallKind.Feature, call.Kind);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x23, 0x00, 0x00, 0x00 }, call.Bytes);
    }

    [Fact]
    public void Profile_is_sl_infinity_while_detached_and_the_attached_row_after()
    {
        var hub = new LianLiHub();
        Assert.Equal(0xA102, hub.Profile.ProductId);
        hub.Attach(new HubTransportSpy(), SlProfile());
        Assert.Equal(0xA100, hub.Profile.ProductId);
        hub.Detach();
        Assert.Equal(0xA102, hub.Profile.ProductId);
    }

    [Fact]
    public void SendStartAction_sends_via_feature_report()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile());

        hub.SendStartAction(0, 4);

        Assert.Single(spy.Calls);
        // L-Connect's SetQuantity is a feature report.
        Assert.Equal(HubTransportSpy.CallKind.Feature, spy.Calls[0].Kind);
    }

    // ── ModelName ──

    [Fact]
    public void ModelName_returns_profile_model_name_when_attached()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile());

        Assert.Equal("SL-Infinity", hub.ModelName);
    }

    [Fact]
    public void ModelName_returns_empty_before_attach()
    {
        var hub = new LianLiHub();
        Assert.Equal("", hub.ModelName);
    }

    [Fact]
    public void ModelName_clears_after_detach()
    {
        var spy = new HubTransportSpy();
        var hub = new LianLiHub();
        hub.Attach(spy, SlInfinityProfile());
        hub.Detach();

        Assert.Equal("", hub.ModelName);
    }
}
