using Nexus.Service.Platform.Linux;
using Nexus.Service.Platform.Linux.Render;
using SkiaSharp;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public sealed class LinuxGpuRenderTests
{
    private const string Profile = "/tmp/profile";

    [Fact]
    public void Software_tier_adds_no_gpu_flags()
    {
        var args = HeadlessBrowser.Arguments(Profile, GpuTier.Software);

        Assert.Equal(HeadlessBrowser.Arguments(Profile), args);
        Assert.DoesNotContain(args, a => a.Contains("angle") || a.Contains("gpu"));
    }

    [Fact]
    public void Vulkan_tier_selects_the_vulkan_angle_backend()
    {
        var args = HeadlessBrowser.Arguments(Profile, GpuTier.Vulkan);

        Assert.Contains("--use-angle=vulkan", args);
        Assert.Contains("--enable-features=Vulkan,VulkanFromANGLE,DefaultANGLEVulkan", args);
        Assert.Contains("--enable-gpu-rasterization", args);
        Assert.DoesNotContain("--use-angle=gl-egl", args);
    }

    [Fact]
    public void Egl_tier_selects_the_gl_egl_angle_backend()
    {
        var args = HeadlessBrowser.Arguments(Profile, GpuTier.Egl);

        Assert.Contains("--use-gl=angle", args);
        Assert.Contains("--use-angle=gl-egl", args);
        Assert.DoesNotContain("--use-angle=vulkan", args);
    }

    [Theory]
    [InlineData(GpuTier.Software)]
    [InlineData(GpuTier.Vulkan)]
    [InlineData(GpuTier.Egl)]
    public void Every_tier_keeps_the_pipe_profile_and_trailing_blank_page(GpuTier tier)
    {
        var args = HeadlessBrowser.Arguments(Profile, tier);

        Assert.Contains("--remote-debugging-pipe", args);
        Assert.Contains($"--user-data-dir={Profile}", args);
        Assert.Equal("about:blank", args[^1]);
    }

    [Theory]
    [InlineData("ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver)", true)]
    [InlineData("ANGLE (Mesa, llvmpipe (LLVM 15.0.7, 256 bits), OpenGL 4.5)", true)]
    [InlineData("Google SwiftShader", true)]
    [InlineData("none", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("ANGLE (NVIDIA, NVIDIA GeForce RTX 5070 (0x00002F04) Vulkan 1.4.329, NVIDIA)", false)]
    [InlineData("ANGLE (AMD, AMD Radeon RX 7800 XT (RADV NAVI32), OpenGL 4.6)", false)]
    [InlineData("ANGLE (Intel, Mesa Intel(R) UHD Graphics 630 (CFL GT2), OpenGL 4.6)", false)]
    public void Software_renderer_detection(string? renderer, bool software) =>
        Assert.Equal(software, GpuTiers.IsSoftwareRenderer(renderer));

    [Fact]
    public async Task Selection_prefers_vulkan_when_it_works()
    {
        var tried = new List<GpuTier>();

        var tier = await GpuTiers.SelectAsync(t =>
        {
            tried.Add(t);
            return Task.FromResult(true);
        });

        Assert.Equal(GpuTier.Vulkan, tier);
        Assert.Equal(new[] { GpuTier.Vulkan }, tried);
    }

    [Fact]
    public async Task Selection_falls_to_egl_when_vulkan_is_software()
    {
        var tried = new List<GpuTier>();

        var tier = await GpuTiers.SelectAsync(t =>
        {
            tried.Add(t);
            return Task.FromResult(t == GpuTier.Egl);
        });

        Assert.Equal(GpuTier.Egl, tier);
        Assert.Equal(new[] { GpuTier.Vulkan, GpuTier.Egl }, tried);
    }

    [Fact]
    public async Task Selection_treats_a_throwing_tier_as_failed_and_ends_on_software()
    {
        var logs = new List<string>();

        var tier = await GpuTiers.SelectAsync(t => throw new IOException("browser died"), logs.Add);

        Assert.Equal(GpuTier.Software, tier);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task Selection_propagates_cancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GpuTiers.SelectAsync(_ => throw new OperationCanceledException()));
    }

    [Fact]
    public void Green_check_accepts_green_and_rejects_black()
    {
        const int size = 4;
        var green = new byte[size * size * 4];
        var black = new byte[size * size * 4];
        for (var i = 0; i < green.Length; i += 4)
        {
            green[i + 1] = 255;
            green[i + 3] = 255;
        }

        Assert.True(LinuxStreamRenderHost.IsGreen(green, size));
        Assert.False(LinuxStreamRenderHost.IsGreen(black, size));
    }

    [Fact]
    public void Decode_handles_jpeg_frames_and_keeps_the_requested_size()
    {
        using var bitmap = new SKBitmap(32, 16);
        bitmap.Erase(new SKColor(0, 255, 0));
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, PanelRenderSession.JpegQuality);
        var base64 = Convert.ToBase64String(data.ToArray());

        var same = PanelRenderSession.DecodeToBgra(base64, 32, 16);
        var rounded = PanelRenderSession.DecodeToBgra(base64, 34, 18);

        Assert.NotNull(same);
        Assert.Equal(32 * 16 * 4, same!.Length);
        Assert.True(same[1] > 200 && same[0] < 60 && same[2] < 60);
        Assert.NotNull(rounded);
        Assert.Equal(34 * 18 * 4, rounded!.Length);
    }

    [Theory]
    [InlineData("[1:2:ERROR:gpu_process_host.cc(990)] GPU process exited unexpectedly: exit_code=139", true)]
    [InlineData("[1:2:ERROR:viz_main_impl.cc(186)] Exiting GPU process due to errors during initialization", true)]
    [InlineData("GPU process isn't usable. Goodbye.", true)]
    [InlineData("[1:2:WARNING:gpu_channel.cc] unrelated warning", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Gpu_failure_line_matching(string? line, bool expected) =>
        Assert.Equal(expected, HeadlessBrowser.IsGpuFailureLine(line));

    [Theory]
    [InlineData(1, 5_000)]
    [InlineData(2, 10_000)]
    [InlineData(3, 20_000)]
    [InlineData(5, 60_000)]
    [InlineData(40, 60_000)]
    public void Session_retry_backs_off_to_a_minute(int streak, int expected) =>
        Assert.Equal(expected, LinuxStreamRenderHost.SessionRetryDelayMs(streak));

    [Fact]
    public void Probe_is_due_only_after_the_pause()
    {
        Assert.False(LinuxStreamRenderHost.ProbeDue(999, 1_000));
        Assert.True(LinuxStreamRenderHost.ProbeDue(1_000, 1_000));
        Assert.True(LinuxStreamRenderHost.ProbeDue(5, 0));
    }
}
