using System;
using System.IO;
using Nexus.Service.Audio;
using Xunit;

namespace Nexus.Service.Tests.Audio;

/// <summary>
/// AudioFilePlayer's path-validation gate runs before any platform dispatch,
/// so these exercise it without ever spawning a real player process (no
/// audio emitted in CI). Real playback is a manual hardware-verification
/// step (see plans), not something this suite can assert on headlessly.
/// </summary>
public sealed class AudioFilePlayerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-audio-player-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Play_NullPath_IsANoOp()
    {
        var player = new AudioFilePlayer();
        player.Play(null, 50);
    }

    [Fact]
    public void Play_EmptyPath_IsANoOp()
    {
        var player = new AudioFilePlayer();
        player.Play("", 50);
    }

    [Fact]
    public void Play_MissingFile_LogsAndReturnsWithoutThrowing()
    {
        var player = new AudioFilePlayer();
        player.Play(Path.Combine(_tempDir, "does-not-exist.wav"), 50);
    }

    [Fact]
    public void Play_MissingFile_VolumeOutOfRange_StillDoesNotThrow()
    {
        var player = new AudioFilePlayer();
        player.Play(Path.Combine(_tempDir, "does-not-exist.wav"), 500);
        player.Play(Path.Combine(_tempDir, "does-not-exist.wav"), -10);
        player.Play(Path.Combine(_tempDir, "does-not-exist.wav"), null);
    }
}
