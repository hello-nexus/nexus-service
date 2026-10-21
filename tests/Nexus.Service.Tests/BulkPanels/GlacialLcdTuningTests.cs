using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nexus.Service.Diagnostics;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public sealed class GlacialLcdTuningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glacial-tuning-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TestableConfigStore _store;

    public GlacialLcdTuningTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TestableConfigStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_are_the_last_field_proven_configuration()
    {
        var v = new GlacialLcdTuning(_store).Current();

        Assert.Equal(19, v.PacePercent);
        Assert.False(v.DoubleBuffer);
        Assert.Equal(4, v.ChunkKb);
        Assert.Equal(65, v.JpegQuality);
        Assert.Equal(100, v.ScalePercent);
        Assert.Equal("send", v.ScaleMode);
        Assert.False(v.ReplyDriven);
        Assert.Equal(30, v.MaxFps);
    }

    [Fact]
    public void The_old_pace_key_is_read_through_until_the_new_one_is_set()
    {
        _store.Update(s => s.Devices.GlacialLcdUploadPacePercent = 40);
        var tuning = new GlacialLcdTuning(_store);
        Assert.Equal(40, tuning.Current().PacePercent);

        tuning.Apply(new GlacialLcdTuningChange { PacePercent = 7 });
        Assert.Equal(7, tuning.Current().PacePercent);

        _store.Update(s => s.Devices.GlacialLcdUploadPacePercent = 900);
        _store.Update(s => s.Devices.GlacialLcdTuning.PacePercent = null);
        Assert.Equal(100, tuning.Current().PacePercent);
    }

    [Theory]
    [InlineData("maxFps", 0.1, false)]
    [InlineData("maxFps", 0.2, true)]
    [InlineData("maxFps", 30.0, true)]
    [InlineData("maxFps", 31.0, false)]
    [InlineData("pace", 101, false)]
    [InlineData("pace", 0, true)]
    [InlineData("scale", 24, false)]
    [InlineData("scale", 25, true)]
    [InlineData("quality", 29, false)]
    [InlineData("quality", 95, true)]
    [InlineData("quality", 96, false)]
    [InlineData("chunk", 0, false)]
    [InlineData("chunk", 16, true)]
    [InlineData("chunk", 17, false)]
    public void Ranges_are_checked(string field, double value, bool valid)
    {
        var change = new GlacialLcdTuningChange();
        switch (field)
        {
            case "maxFps": change.MaxFps = value; break;
            case "pace": change.PacePercent = (int)value; break;
            case "scale": change.ScalePercent = (int)value; break;
            case "quality": change.JpegQuality = (int)value; break;
            case "chunk": change.ChunkKb = (int)value; break;
        }
        Assert.Equal(valid, GlacialLcdTuning.Validate(change) is null);
    }

    [Fact]
    public void A_scale_mode_other_than_send_or_upscale_is_refused()
    {
        Assert.NotNull(GlacialLcdTuning.Validate(new GlacialLcdTuningChange { ScaleMode = "x" }));
        Assert.Null(GlacialLcdTuning.Validate(new GlacialLcdTuningChange { ScaleMode = "upscale" }));
    }

    [Fact]
    public void A_change_is_persisted_logged_and_gets_a_new_settings_id()
    {
        string logs = Path.Combine(_dir, "logs");
        GlacialLcdLog.UseDirectory(logs);
        try
        {
            var tuning = new GlacialLcdTuning(_store);
            int before = tuning.Current().Id;

            var after = tuning.Apply(new GlacialLcdTuningChange { ChunkKb = 8, ReplyDriven = true });

            Assert.True(after.Id > before);
            Assert.Equal(8, _store.Load().Devices.GlacialLcdTuning.ChunkKb);
            string text = File.ReadAllText(Path.Combine(logs, GlacialLcdLog.FileName));
            Assert.Contains("chunkKb 4 -> 8", text);
            Assert.Contains("replyDriven False -> True", text);
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void The_rate_counts_uploads_in_the_last_ten_seconds_and_reset_clears_the_stats()
    {
        long now = 0;
        var tuning = new GlacialLcdTuning(_store, () => now);
        for (int i = 0; i < 5; i++)
        {
            tuning.UploadOk(900, 20_000, 30);
        }
        Assert.Equal(0.5, tuning.Stats().UploadsPerSec);
        now = 11_000;
        Assert.Equal(0, tuning.Stats().UploadsPerSec);

        tuning.UploadFailed("boom");
        tuning.Stall("silent");
        Assert.True(tuning.Stats().Stalled);
        tuning.ResetStats();
        var st = tuning.Stats();
        Assert.Equal(0, st.UploadsOk);
        Assert.Equal(0, st.UploadsFailed);
        Assert.Null(st.LastError);
        Assert.True(st.Stalled);
        tuning.Resume();
        Assert.False(tuning.Stats().Stalled);
    }

    [Fact]
    public void The_log_rotates_into_a_second_file_and_stays_capped()
    {
        string logs = Path.Combine(_dir, "rot");
        GlacialLcdLog.UseDirectory(logs, maxFileBytes: 400);
        try
        {
            for (int i = 0; i < 40; i++)
            {
                GlacialLcdLog.Write("line " + i + " " + new string('x', 30));
            }
            var main = new FileInfo(Path.Combine(logs, GlacialLcdLog.FileName));
            var old = new FileInfo(Path.Combine(logs, GlacialLcdLog.RotatedFileName));
            Assert.True(main.Exists && old.Exists);
            Assert.True(main.Length <= 400 && old.Length <= 400);
            Assert.Contains("line 39", File.ReadAllText(main.FullName));
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }
    }

    [Fact]
    public void The_support_bundle_includes_both_glacial_logs()
    {
        string logs = Path.Combine(_dir, "bundle-logs");
        GlacialLcdLog.UseDirectory(logs, maxFileBytes: 200);
        try
        {
            for (int i = 0; i < 20; i++)
            {
                GlacialLcdLog.Write("entry " + i + " " + new string('y', 30));
            }
        }
        finally
        {
            GlacialLcdLogTestSetup.Restore();
        }

        var bytes = SupportBundleBuilder.Build(new SupportBundleBuilder.Sources
        {
            LogsDirectory = logs,
            UpdatesDirectory = Path.Combine(_dir, "u"),
            OpenRgbConfigDirectory = Path.Combine(_dir, "o"),
            Info = new SupportInfo { Version = "v1", MachineName = "box" },
            StartupSnapshot = "snap",
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("logs/" + GlacialLcdLog.FileName, names);
        Assert.Contains("logs/" + GlacialLcdLog.RotatedFileName, names);
    }

    [Fact]
    public void A_scaled_size_is_kept_to_whole_blocks()
    {
        Assert.Equal((360, 640), GlacialLcdScaler.ScaledSize(720, 1280, 50));
        Assert.Equal((720, 1280), GlacialLcdScaler.ScaledSize(720, 1280, 100));
        var (w, h) = GlacialLcdScaler.ScaledSize(720, 1280, 25);
        Assert.Equal(0, w % 8);
        Assert.Equal(0, h % 8);
    }
}
