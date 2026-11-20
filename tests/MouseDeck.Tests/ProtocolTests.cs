using MouseDeck.Core.Models;
using MouseDeck.Core.Protocols;
using MouseDeck.Core.Transport;
using Xunit;

namespace MouseDeck.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void CompX_BuildPacket_ComputesCorrectCompX55Checksum()
    {
        var pkt = CompXProtocol.BuildPacket(0x04, 0x0000, []);
        Assert.Equal(CompXProtocol.PacketLength, pkt.Length);
        Assert.Equal(0x08, pkt[0]);
        Assert.Equal(0x04, pkt[1]);

        int sum = 0;
        for (int i = 0; i < 16; i++) sum += pkt[i];
        byte expectedChecksum = (byte)((0x55 - (sum & 0xFF)) & 0xFF);
        Assert.Equal(expectedChecksum, pkt[16]);
    }

    [Fact]
    public void CompX_ParseBatteryResponse_ParsesAwakeChargingAndVoltage()
    {
        // ReportId=0x08, Cmd=0x04, ..., [6]=78%, [7]=1 (charging), [8..9]=0x0F78 (3960 mV)
        var rx = new byte[17];
        rx[0] = 0x08;
        rx[1] = 0x04;
        rx[6] = 78;
        rx[7] = 1;
        rx[8] = 0x0F;
        rx[9] = 0x78;

        var reading = CompXProtocol.ParseBatteryResponse(rx);
        Assert.NotNull(reading);
        Assert.True(reading!.IsMouseAwake);
        Assert.Equal(78, reading.Percentage);
        Assert.True(reading.IsCharging);
        Assert.Equal(3960, reading.VoltageMv);
    }

    [Fact]
    public void CompX_ParseBatteryResponse_DetectsDeepSleepWhenZeroed()
    {
        var rx = new byte[17];
        rx[0] = 0x08;
        rx[1] = 0x04;

        var reading = CompXProtocol.ParseBatteryResponse(rx);
        Assert.NotNull(reading);
        Assert.False(reading!.IsMouseAwake);
    }

    [Fact]
    public void Nordic_BuildPacket_And_ParseBatteryResponse_Works()
    {
        var tx = NordicHidProtocol.BuildPacket(64, 0x02, []);
        Assert.Equal(64, tx.Length);
        Assert.Equal(NordicHidProtocol.DefaultReportId, tx[0]);
        Assert.Equal(0xAA, tx[1]);
        Assert.Equal(HidPacketHelper.ComputeXor8(tx.AsSpan(0, 63)), tx[63]);

        var rx = new byte[64];
        rx[0] = NordicHidProtocol.DefaultReportId;
        rx[1] = 0xAA;
        rx[4] = 1;    // awake
        rx[5] = 63;   // 63%
        rx[6] = 0;    // not charging
        rx[7] = 0x58; // 3928 mV LE = 0x0F58
        rx[8] = 0x0F;

        var reading = NordicHidProtocol.ParseBatteryResponse(rx);
        Assert.NotNull(reading);
        Assert.True(reading!.IsMouseAwake);
        Assert.Equal(63, reading.Percentage);
        Assert.False(reading.IsCharging);
        Assert.Equal(3928, reading.VoltageMv);
    }

    [Fact]
    public void ProtocolRegistry_SelectsRealHardwareOverVirtualAndMatchesCorrectDriver()
    {
        var virtualProto = new VirtualMockProtocol();
        var registry = new ProtocolRegistry([new CompXProtocol(), new NordicHidProtocol(), virtualProto]);

        var compxDevice = new MouseDeviceInfo
        {
            DevicePath = @"\\?\hid#vid_1d57&pid_fa60&mi_01",
            VendorId = 0x1D57,
            ProductId = 0xFA60,
            UsagePage = 0xFF02,
            Usage = 0x0002,
            FeatureReportLength = 17,
            CanOpenReadWrite = true,
            ProductName = "Attack Shark X3"
        };

        var nordicDevice = new MouseDeviceInfo
        {
            DevicePath = @"\\?\hid#vid_36a7&pid_0001&mi_02",
            VendorId = 0x36A7,
            ProductId = 0x0001,
            UsagePage = 0xFF00,
            Usage = 0x0001,
            OutputReportLength = 64,
            InputReportLength = 64,
            CanOpenReadWrite = true,
            ProductName = "Scyrox V8 8K"
        };

        Assert.Equal("compx-cx52850", registry.FindBestProtocol(compxDevice)?.ProtocolId);
        Assert.Equal("nordic-nrf52840", registry.FindBestProtocol(nordicDevice)?.ProtocolId);

        var selected = registry.SelectPrimaryDevice([VirtualMockProtocol.VirtualDevice, compxDevice]);
        Assert.NotNull(selected);
        Assert.Equal(compxDevice.DevicePath, selected!.Device.DevicePath);
    }

    [Fact]
    public void HidPacketHelper_ParsesSpacedAndCompactHexStrings()
    {
        Assert.Equal(new byte[] { 0x08, 0x04, 0xFF }, HidPacketHelper.ParseHexString("08 04 FF"));
        Assert.Equal(new byte[] { 0x08, 0x04, 0xFF }, HidPacketHelper.ParseHexString("0x08, 0x04, 0xFF"));
        Assert.Equal(new byte[] { 0x08, 0x04, 0xFF }, HidPacketHelper.ParseHexString("0804FF"));
    }

    [Fact]
    public void MouseProfile_EstimatesBatteryHoursAndHashesIndependentXY()
    {
        var p = new MouseProfile { PollingRateHz = 1000, MotionSync = false, HighSpeedMode = false };
        Assert.Equal(48.0, p.EstimateRemainingHours(100), precision: 1);
        Assert.Equal(1.0, p.PollingLatencyMs, precision: 2);

        p.PollingRateHz = 4000;
        Assert.Equal(0.25, p.PollingLatencyMs, precision: 3);
        Assert.True(p.EstimateRemainingHours(100) < 25);

        string h1 = p.ComputeSyncHash();
        p.IndependentXY = true;
        p.DpiStages[0].DpiY = 1200;
        Assert.NotEqual(h1, p.ComputeSyncHash());
    }

    private sealed class RecordingHidSession : IHidSession
    {
        public MouseDeviceInfo Device { get; } = new()
        {
            DevicePath = "test://compx",
            VendorId = 0x373B,
            ProductId = 0x1040,
            UsagePage = 0xFF02,
            Usage = 0x0002,
            InputReportLength = 17,
            OutputReportLength = 17,
            CanOpenReadWrite = true
        };
        public bool IsOpen => true;
        public List<byte[]> SentPackets { get; } = [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<bool> SetFeatureReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default) => Task.FromResult(true);
        public Task<byte[]?> GetFeatureReportAsync(byte reportId, int expectedLength, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> WriteOutputReportAsync(ReadOnlyMemory<byte> report, int timeoutMs = 250, CancellationToken ct = default)
        {
            SentPackets.Add(report.ToArray());
            return Task.FromResult(true);
        }
        public Task<byte[]?> ReadInputReportAsync(int expectedLength, int timeoutMs = 250, CancellationToken ct = default)
        {
            // Echo ACK with ReportId 0x08 + CmdWriteEeprom 0x07
            var ack = new byte[17];
            ack[0] = 0x08;
            ack[1] = 0x07;
            return Task.FromResult<byte[]?>(ack);
        }
    }

    [Fact]
    public async Task CompX_DiffedApplyProfile_WritesOnlyChangedPollingRateRegister()
    {
        var proto = new CompXProtocol();
        await using var session = new RecordingHidSession();

        var baseline = new MouseProfile { PollingRateHz = 2000 };
        var updated = baseline.Clone();
        updated.PollingRateHz = 8000;

        bool ok = await proto.ApplyProfileAsync(session, updated, null, CancellationToken.None, baseline);
        Assert.True(ok);
        Assert.Single(session.SentPackets);

        var pkt = session.SentPackets[0];
        Assert.Equal(0x08, pkt[0]);
        Assert.Equal(0x07, pkt[1]); // Write EEPROM
        Assert.Equal(0x00, pkt[3]); // Addr Hi
        Assert.Equal(0x00, pkt[4]); // Addr Lo (Polling Rate)
        Assert.Equal(0x40, pkt[6]); // 8000 Hz = 0x40
        Assert.Equal(0x15, pkt[7]); // 0x55 - 0x40 = 0x15
    }
}
