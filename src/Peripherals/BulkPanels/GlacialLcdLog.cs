using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// A dedicated log for the Glacial LCD Nexus panel upload path, rotated across two files so the
/// pair stays under a fixed size. Lines are queued and written by one background thread, so a
/// caller never waits on the file. Silent until the Nexus panel gate enables it. The support
/// bundle takes every *.log in the logs directory.
/// </summary>
public static class GlacialLcdLog
{
    public const string FileName = "glacial-lcd.log";
    public const string RotatedFileName = "glacial-lcd.1.log";
    public const long DefaultMaxFileBytes = 5L * 1024 * 1024;

    private readonly record struct Line(string Dir, long MaxFileBytes, string Text);

    private static readonly object Gate = new();
    private static readonly Queue<Line> Pending = new();
    private static readonly AsyncLocal<(string Dir, long Max)?> Scoped = new();
    private static Thread? _writer;
    private static bool _busy;
    private static (string Dir, long Max)? _default;

    /// <summary>False until the Nexus panel gate is on, so an install with the gate off never creates the file.</summary>
    public static volatile bool Enabled;

    /// <summary>Test seam: keeps the log on whatever tests that build the service set <see cref="Enabled"/> to.</summary>
    internal static volatile bool ForceOn;

    /// <summary>Test seam: this directory and cap for the calling context and the threads it starts.</summary>
    internal static void UseDirectory(string? dir, long maxFileBytes = DefaultMaxFileBytes) =>
        Scoped.Value = dir is null ? null : (dir, maxFileBytes);

    /// <summary>Test seam: where lines go when no scoped directory applies.</summary>
    internal static void SetDefaultDirectory(string dir, long maxFileBytes = DefaultMaxFileBytes)
    {
        lock (Gate)
        {
            _default = (dir, maxFileBytes);
        }
    }

    public static void Write(string message)
    {
        if (!Enabled && !ForceOn)
        {
            return;
        }
        var target = Scoped.Value;
        lock (Gate)
        {
            target ??= _default;
            string dir = target?.Dir ?? ServiceLog.LogsDirectory;
            Pending.Enqueue(new Line(dir, target?.Max ?? DefaultMaxFileBytes, $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {message}\n"));
            if (_writer is null)
            {
                _writer = new Thread(Drain) { IsBackground = true, Name = "glacial-lcd-log" };
                _writer.Start();
            }
            Monitor.Pulse(Gate);
        }
    }

    /// <summary>Waits until every queued line is on disk, or the timeout.</summary>
    public static void Flush(int timeoutMs = 10_000)
    {
        lock (Gate)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while ((Pending.Count > 0 || _busy) && Environment.TickCount64 < deadline)
            {
                Monitor.Wait(Gate, 50);
            }
        }
    }

    private static void Drain()
    {
        var batch = new List<Line>();
        while (true)
        {
            lock (Gate)
            {
                while (Pending.Count == 0)
                {
                    _busy = false;
                    Monitor.PulseAll(Gate);
                    Monitor.Wait(Gate);
                }
                _busy = true;
                batch.AddRange(Pending);
                Pending.Clear();
            }
            try
            {
                Append(batch);
            }
            catch (Exception)
            {
            }
            batch.Clear();
        }
    }

    private static void Append(List<Line> batch)
    {
        int i = 0;
        while (i < batch.Count)
        {
            var first = batch[i];
            Directory.CreateDirectory(first.Dir);
            string path = Path.Combine(first.Dir, FileName);
            var info = new FileInfo(path);
            long size = info.Exists ? info.Length : 0;
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            while (i < batch.Count && batch[i].Dir == first.Dir)
            {
                var bytes = Encoding.UTF8.GetBytes(batch[i].Text);
                if (size > 0 && size + bytes.Length > batch[i].MaxFileBytes)
                {
                    fs.Dispose();
                    File.Move(path, Path.Combine(first.Dir, RotatedFileName), overwrite: true);
                    size = 0;
                    Append(batch.GetRange(i, batch.Count - i));
                    return;
                }
                fs.Write(bytes, 0, bytes.Length);
                size += bytes.Length;
                i++;
            }
        }
    }
}
