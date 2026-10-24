using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Tests.DeepCool;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>Uploads through the route to a fake screen, so the swap that keeps a new picture shown runs end to end.</summary>
public partial class DeepCoolMystiqueRoutesTests
{
    private sealed class MystiqueScreenFactory : NexusAppFactory
    {
        public MystiqueWorkerTests.FakeFactory Pipes { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<MystiqueWorker>();
                services.AddSingleton(sp => new MystiqueWorker(Pipes, sp.GetRequiredService<DeviceControlGate>(),
                    new HardwarePresence(new MystiqueWorkerTests.FakeUsb(true)), sp.GetRequiredService<IConfigStore>(),
                    new MystiqueWorkerTests.FakeSensors(), connectPollMs: 20, tickMs: 20));
            });
        }
    }

    private sealed class Screen : IAsyncDisposable
    {
        private readonly string _dir;

        private Screen(string dir)
        {
            _dir = dir;
            Factory.Server.PreserveExecutionContext = true;
            Client = Client(Factory);
            Store = Factory.Services.GetRequiredService<IConfigStore>();
            Factory.Services.GetRequiredService<DeviceControlGate>().SetEnabled(MystiqueWorker.HandlerId, true);
            Worker = Factory.Services.GetRequiredService<MystiqueWorker>();
        }

        public MystiqueScreenFactory Factory { get; } = new();
        public HttpClient Client { get; }
        public IConfigStore Store { get; }
        public MystiqueWorker Worker { get; }
        public MystiqueWorkerTests.RecordingPipe Pipe => Factory.Pipes.Pipe!;

        /// <summary>Starts a connected screen; the caller has pointed the media folder at <paramref name="dir"/>.</summary>
        public static async Task<Screen> StartAsync(string dir)
        {
            var screen = new Screen(dir);
            await screen.Worker.StartAsync(CancellationToken.None);
            await WaitFor(() => screen.Worker.IsConnected);
            return screen;
        }

        public string MediaFile(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

        public async Task<JsonElement> UploadAsync(byte[] png, string replace = "")
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(png), "file", "p.png" },
                { new StringContent(DeepCoolMystiqueMediaItem.KindPicture), "kind" },
            };
            if (replace != "")
            {
                form.Add(new StringContent(replace), "replace");
            }
            var res = await Client.PostAsync("/devices/deepcool-mystique/media", form);
            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return body.RootElement.Clone();
        }

        public DeepCoolMystiqueSettings Settings()
        {
            var m = new DeepCoolMystiqueSettings();
            Store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
            return m;
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Factory.Dispose();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
    }

    /// <summary>Points the media folder at a new temp folder for this test's flow.</summary>
    private static string MediaRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mystique-{Guid.NewGuid():N}");
        DevicesRoutes.MystiqueMediaRootForTests.Value = dir;
        return dir;
    }

    private static string PictureId(byte[] png) => MystiqueMedia.IdOf(MystiqueMedia.KindPicture, png);

    /// <summary>Two different pictures, the first with the lower id.</summary>
    private static (byte[] Low, byte[] High) PicturePair()
    {
        var a = MystiqueMediaTests.Png(120, 120);
        var b = MystiqueMediaTests.Png(120, 120, shift: 7);
        return string.CompareOrdinal(PictureId(a), PictureId(b)) < 0 ? (a, b) : (b, a);
    }

    private static List<byte[]> WritesSince(Screen screen, int mark) => screen.Pipe.Writes().Skip(mark).ToList();

    private static int Commands(List<byte[]> writes, byte command) =>
        writes.Count(w => w.Length == MystiqueProtocol.FrameLength && w[2] == command);

    private static void AssertOneSourceAfterTheLastStream(List<byte[]> writes)
    {
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdMediaSource));
        int source = writes.FindIndex(w => w.Length == MystiqueProtocol.FrameLength && w[2] == MystiqueProtocol.CmdMediaSource);
        Assert.True(source > writes.FindLastIndex(w => w.Length == MystiqueMedia.FinishLength));
    }

    private static void AssertHeld(DeepCoolMystiqueSettings m, params (string Id, string ScreenId)[] expected)
    {
        foreach (var (id, screenId) in expected)
        {
            var item = m.Media.Single(i => i.Id == id);
            Assert.Equal(screenId, DevicesRoutes.MystiqueScreenId(item));
        }
    }

    [FfmpegFact]
    public async Task An_upload_above_the_lowest_picture_id_swaps_into_it_with_one_source_command_after_the_swap()
    {
        var dir = MediaRoot();
        await using var screen = await Screen.StartAsync(dir);
        var (low, high) = PicturePair();
        Assert.False((await screen.UploadAsync(low)).GetProperty("error").GetBoolean());
        int mark = screen.Pipe.Writes().Count;

        var res = await screen.UploadAsync(high);
        await TwoTicks(screen);

        Assert.False(res.GetProperty("error").GetBoolean());
        Assert.Equal(PictureId(high), res.GetProperty("id").GetString());
        var writes = WritesSince(screen, mark);
        Assert.Equal(3, Commands(writes, MystiqueProtocol.CmdUploadStart));
        AssertOneSourceAfterTheLastStream(writes);
        var m = screen.Settings();
        AssertHeld(m, (PictureId(high), PictureId(low)), (PictureId(low), PictureId(high)));
        Assert.True(m.Media.TrueForAll(i => !i.Unsent));
        Assert.Equal(PictureId(high), DevicesRoutes.MystiqueShownPicture(m));
    }

    [FfmpegFact]
    public async Task A_replaced_picture_above_the_lowest_id_swaps_into_it()
    {
        var dir = MediaRoot();
        await using var screen = await Screen.StartAsync(dir);
        var (low, high) = PicturePair();
        await screen.UploadAsync(high);
        await screen.UploadAsync(low);
        int mark = screen.Pipe.Writes().Count;

        var res = await screen.UploadAsync(MystiqueMediaTests.Png(120, 120, shift: 14), replace: PictureId(high));
        await TwoTicks(screen);

        Assert.False(res.GetProperty("error").GetBoolean());
        Assert.Equal(PictureId(high), res.GetProperty("id").GetString());
        var writes = WritesSince(screen, mark);
        Assert.Equal(3, Commands(writes, MystiqueProtocol.CmdUploadStart));
        AssertOneSourceAfterTheLastStream(writes);
        var m = screen.Settings();
        AssertHeld(m, (PictureId(high), PictureId(low)), (PictureId(low), PictureId(high)));
        Assert.Equal(PictureId(high), DevicesRoutes.MystiqueShownPicture(m));
    }

    [FfmpegFact]
    public async Task A_swap_that_fails_after_the_upload_landed_reports_success_and_leaves_the_slot_picture_unsent()
    {
        var dir = MediaRoot();
        await using var screen = await Screen.StartAsync(dir);
        var (low, high) = PicturePair();
        await screen.UploadAsync(low);
        var before = screen.Pipe;
        int mark = before.Writes().Count;
        int announces = 0;
        screen.Factory.Pipes.Silence = c => c == MystiqueProtocol.CmdUploadStart && Interlocked.Increment(ref announces) > 1;

        var res = await screen.UploadAsync(high);
        screen.Factory.Pipes.Silence = null;
        await WaitFor(() => screen.Worker.IsConnected && screen.Pipe != before);
        await TwoTicks(screen);

        Assert.False(res.GetProperty("error").GetBoolean());
        Assert.Equal(PictureId(high), res.GetProperty("id").GetString());
        var m = screen.Settings();
        AssertHeld(m, (PictureId(high), PictureId(high)), (PictureId(low), PictureId(low)));
        Assert.False(m.Media.Single(i => i.Id == PictureId(high)).Unsent);
        Assert.True(m.Media.Single(i => i.Id == PictureId(low)).Unsent);
        Assert.Equal(DeepCoolMystiqueSettings.SourceImages, m.MediaSource);
        Assert.Equal(0, Commands(before.Writes().Skip(mark).ToList(), MystiqueProtocol.CmdMediaSource));
        Assert.Equal(1, Commands(screen.Pipe.Writes(), MystiqueProtocol.CmdMediaSource));
    }

    private const string Slot = "2101b6e6277841daa821e8d79a5c528b";
    private const string Raised = "acd9325521f3493c86bfc0b0aa85658d";

    /// <summary>A screen holding a picture under <see cref="Slot"/> while a GIF plays, with that applied, and a swap planned into it.</summary>
    private static async Task<(List<MystiqueMediaPackage>, List<(string, string)>)> SeedSwapAsync(Screen screen)
    {
        screen.Store.Update(s =>
        {
            var m = s.Devices.DeepCoolMystique;
            m.Media = new()
            {
                new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Slot },
                new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Raised },
            };
            m.PictureIds = new() { Slot, Raised };
            m.MediaSource = DeepCoolMystiqueSettings.SourceGif;
            m.SourceEdited = true;
        });
        screen.Worker.RequestApply();
        await TwoTicks(screen);
        var package = new MystiqueMediaPackage(MystiqueMedia.KindPicture, Slot, new[] { new byte[64] });
        return (new() { package }, new() { (Raised, Slot) });
    }

    [Fact]
    public async Task A_swap_the_busy_screen_never_takes_records_the_held_source_for_one_source_command_and_marks_nothing_unsent()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var swap = await SeedSwapAsync(screen);
        int mark = screen.Pipe.Writes().Count;
        screen.Pipe.HoldStream = true;
        var busy = screen.Worker.UploadAsync(swap.Item1, () => null, CancellationToken.None);
        Assert.True(await Task.Run(() => screen.Pipe.Streaming.Wait(TimeSpan.FromSeconds(10))));

        await DevicesRoutes.SwapMystiqueUploadAsync(screen.Store, screen.Worker, swap, false, DevicesRoutes.MystiqueSourceChoices, 0, 0, CancellationToken.None);
        var m = screen.Settings();
        screen.Pipe.Release.Set();
        await busy;
        await TwoTicks(screen);

        Assert.Equal(DeepCoolMystiqueSettings.SourceImages, m.MediaSource);
        Assert.True(m.Media.TrueForAll(i => !i.Unsent));
        var writes = WritesSince(screen, mark);
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdUploadStart));
        var sources = writes.FindAll(w => w.Length == MystiqueProtocol.FrameLength && w[2] == MystiqueProtocol.CmdMediaSource);
        Assert.Equal(new[] { MystiqueProtocol.BuildMediaSource(DeepCoolMystiqueSettings.SourceImages) }, sources);
    }

    [Fact]
    public async Task With_no_screen_connected_a_swap_records_the_held_source_and_marks_nothing_unsent()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var swap = await SeedSwapAsync(screen);
        await screen.Worker.StopAsync(CancellationToken.None);

        await DevicesRoutes.SwapMystiqueUploadAsync(screen.Store, screen.Worker, swap, false, DevicesRoutes.MystiqueSourceChoices, 0, 0, CancellationToken.None);

        var m = screen.Settings();
        Assert.Equal(DeepCoolMystiqueSettings.SourceImages, m.MediaSource);
        Assert.True(m.Media.TrueForAll(i => !i.Unsent));
    }

    [Fact]
    public async Task A_source_chosen_between_the_upload_and_the_swap_skips_the_swap_and_sends_nothing_over_it()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var swap = await SeedSwapAsync(screen);
        int mark = screen.Pipe.Writes().Count;

        await DevicesRoutes.SwapMystiqueUploadAsync(screen.Store, screen.Worker, swap, false, DevicesRoutes.MystiqueSourceChoices - 1, 0, 0, CancellationToken.None);
        await TwoTicks(screen);

        var writes = WritesSince(screen, mark);
        Assert.Equal(0, Commands(writes, MystiqueProtocol.CmdUploadStart));
        Assert.Equal(0, Commands(writes, MystiqueProtocol.CmdMediaSource));
        var m = screen.Settings();
        Assert.Equal(DeepCoolMystiqueSettings.SourceGif, m.MediaSource);
        Assert.True(m.Media.TrueForAll(i => !i.Unsent));
    }

    [FfmpegFact]
    public async Task An_upload_whose_slot_picture_cannot_be_rebuilt_keeps_its_own_id_and_forgets_nothing()
    {
        var dir = MediaRoot();
        await using var screen = await Screen.StartAsync(dir);
        var (low, high) = PicturePair();
        await screen.UploadAsync(low);
        File.Delete(screen.MediaFile("streams", PictureId(low) + ".bin"));
        foreach (var kept in Directory.GetFiles(screen.MediaFile("sources"), PictureId(low) + ".*"))
        {
            File.Delete(kept);
        }
        int mark = screen.Pipe.Writes().Count;

        var res = await screen.UploadAsync(high);
        await TwoTicks(screen);

        Assert.False(res.GetProperty("error").GetBoolean());
        var writes = WritesSince(screen, mark);
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdUploadStart));
        AssertOneSourceAfterTheLastStream(writes);
        var m = screen.Settings();
        AssertHeld(m, (PictureId(high), PictureId(high)), (PictureId(low), PictureId(low)));
        Assert.True(m.Media.TrueForAll(i => !i.Unsent));
    }

    [FfmpegFact]
    public async Task A_source_chosen_during_the_upload_skips_the_swap_and_stays()
    {
        var dir = MediaRoot();
        await using var screen = await Screen.StartAsync(dir);
        var (low, high) = PicturePair();
        await screen.UploadAsync(low);
        int mark = screen.Pipe.Writes().Count;
        screen.Pipe.HoldStream = true;

        var upload = screen.UploadAsync(high);
        Assert.True(await Task.Run(() => screen.Pipe.Streaming.Wait(TimeSpan.FromSeconds(10))));
        var chose = await screen.Client.PutAsync("/devices/deepcool-mystique/settings", Json("{\"mediaSource\":\"gif\"}"));
        screen.Pipe.Release.Set();
        var res = await upload;
        await TwoTicks(screen);

        Assert.True(chose.IsSuccessStatusCode);
        Assert.False(res.GetProperty("error").GetBoolean());
        var writes = WritesSince(screen, mark);
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdUploadStart));
        var gif = MystiqueProtocol.BuildMediaSource(DeepCoolMystiqueSettings.SourceGif);
        var sources = writes.FindAll(w => w.Length == MystiqueProtocol.FrameLength && w[2] == MystiqueProtocol.CmdMediaSource);
        Assert.NotEmpty(sources);
        Assert.All(sources, w => Assert.Equal(gif, w));
        var m = screen.Settings();
        Assert.Equal(DeepCoolMystiqueSettings.SourceGif, m.MediaSource);
        AssertHeld(m, (PictureId(high), PictureId(high)), (PictureId(low), PictureId(low)));
    }

    /// <summary>Waits out two worker passes, each of which applies any pending settings before it polls.</summary>
    private static Task TwoTicks(Screen screen)
    {
        int mark = screen.Pipe.Writes().Count;
        return WaitFor(() => Commands(WritesSince(screen, mark), MystiqueProtocol.CmdPoll) >= 2);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }
}
