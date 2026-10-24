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

/// <summary>Uploads and shows through the routes to a fake screen, so the one picture slot runs end to end.</summary>
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

        public async Task<JsonElement> ShowAsync(string id)
        {
            var res = await Client.PostAsync($"/devices/deepcool-mystique/media/{id}/show", null);
            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return body.RootElement.Clone();
        }

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

    private static bool Ok(JsonElement res) => !res.GetProperty("error").GetBoolean();

    [FfmpegFact]
    public async Task The_first_picture_after_remove_all_creates_the_slot_and_sends_no_source_command()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        Assert.Equal(System.Net.HttpStatusCode.OK, (await screen.Client.DeleteAsync("/devices/deepcool-mystique/media")).StatusCode);
        var picture = MystiqueMediaTests.Png(120, 120);
        int mark = screen.Pipe.Writes().Count;

        var res = await screen.UploadAsync(picture);
        await TwoTicks(screen);

        Assert.True(Ok(res));
        var writes = WritesSince(screen, mark);
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdUploadStart));
        Assert.Equal(0, Commands(writes, MystiqueProtocol.CmdMediaSource));
        var m = screen.Settings();
        Assert.Equal((PictureId(picture), PictureId(picture)), (m.PictureSlot, m.ShownPicture));
        Assert.Equal(DeepCoolMystiqueSettings.SourceImages, m.MediaSource);
        Assert.False(m.GifSlotUnsure);
    }

    [FfmpegFact]
    public async Task A_later_upload_replaces_the_slot_with_one_source_command_after_it_and_both_stay_listed()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var (first, second) = PicturePair();
        Assert.True(Ok(await screen.UploadAsync(first)));
        int mark = screen.Pipe.Writes().Count;

        var res = await screen.UploadAsync(second);
        await TwoTicks(screen);

        Assert.True(Ok(res));
        Assert.Equal(PictureId(second), res.GetProperty("id").GetString());
        var writes = WritesSince(screen, mark);
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdUploadStart));
        AssertOneSourceAfterTheLastStream(writes);
        var m = screen.Settings();
        Assert.Equal((PictureId(first), PictureId(second)), (m.PictureSlot, m.ShownPicture));
        Assert.Equal(new[] { PictureId(first), PictureId(second) }, m.Media.ConvertAll(i => i.Id));
    }

    [FfmpegFact]
    public async Task A_click_streams_into_the_slot_and_a_click_on_the_shown_picture_sends_only_a_changed_source()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var (first, second) = PicturePair();
        await screen.UploadAsync(first);
        await screen.UploadAsync(second);
        int mark = screen.Pipe.Writes().Count;

        Assert.True(Ok(await screen.ShowAsync(PictureId(first))));
        await TwoTicks(screen);
        var writes = WritesSince(screen, mark);
        Assert.Equal(1, Commands(writes, MystiqueProtocol.CmdUploadStart));
        AssertOneSourceAfterTheLastStream(writes);
        var m = screen.Settings();
        Assert.Equal((PictureId(first), PictureId(first)), (m.PictureSlot, m.ShownPicture));

        mark = screen.Pipe.Writes().Count;
        Assert.True(Ok(await screen.ShowAsync(PictureId(first))));
        await TwoTicks(screen);
        writes = WritesSince(screen, mark);
        Assert.Equal(0, Commands(writes, MystiqueProtocol.CmdUploadStart));
        Assert.Equal(0, Commands(writes, MystiqueProtocol.CmdMediaSource));

        Assert.True((await screen.Client.PutAsync("/devices/deepcool-mystique/settings", Json("{\"mediaSource\":\"gif\"}"))).IsSuccessStatusCode);
        await TwoTicks(screen);
        mark = screen.Pipe.Writes().Count;
        Assert.True(Ok(await screen.ShowAsync(PictureId(first))));
        await TwoTicks(screen);
        writes = WritesSince(screen, mark);
        Assert.Equal(0, Commands(writes, MystiqueProtocol.CmdUploadStart));
        var sources = writes.FindAll(w => w.Length == MystiqueProtocol.FrameLength && w[2] == MystiqueProtocol.CmdMediaSource);
        Assert.Equal(new[] { MystiqueProtocol.BuildMediaSource(DeepCoolMystiqueSettings.SourceImages) }, sources);
    }

    [FfmpegFact]
    public async Task A_failed_show_leaves_the_slot_broken_so_the_next_click_streams_it_again()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var (first, second) = PicturePair();
        await screen.UploadAsync(first);
        await screen.UploadAsync(second);
        var before = screen.Pipe;
        int mark = before.Writes().Count;
        screen.Factory.Pipes.Silence = c => c == MystiqueProtocol.CmdUploadStart;

        var failed = await screen.ShowAsync(PictureId(first));
        screen.Factory.Pipes.Silence = null;
        await WaitFor(() => screen.Worker.IsConnected && screen.Pipe != before);

        Assert.False(Ok(failed));
        Assert.Equal(0, Commands(before.Writes().Skip(mark).ToList(), MystiqueProtocol.CmdMediaSource));
        var m = screen.Settings();
        Assert.Equal((PictureId(first), ""), (m.PictureSlot, m.ShownPicture));

        mark = screen.Pipe.Writes().Count;
        Assert.True(Ok(await screen.ShowAsync(PictureId(first))));
        Assert.Equal(1, Commands(WritesSince(screen, mark), MystiqueProtocol.CmdUploadStart));
        Assert.Equal(PictureId(first), screen.Settings().ShownPicture);
    }

    [FfmpegFact]
    public async Task A_failed_upload_lists_nothing_and_leaves_the_slot_broken()
    {
        await using var screen = await Screen.StartAsync(MediaRoot());
        var (first, second) = PicturePair();
        await screen.UploadAsync(first);
        screen.Factory.Pipes.Silence = c => c == MystiqueProtocol.CmdUploadStart;

        var res = await screen.UploadAsync(second);
        screen.Factory.Pipes.Silence = null;

        Assert.False(Ok(res));
        var m = screen.Settings();
        Assert.Equal(new[] { PictureId(first) }, m.Media.ConvertAll(i => i.Id));
        Assert.Equal((PictureId(first), ""), (m.PictureSlot, m.ShownPicture));
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
