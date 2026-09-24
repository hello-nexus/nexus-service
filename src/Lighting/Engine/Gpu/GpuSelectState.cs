using System;
using System.Globalization;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// The persisted render-GPU decision. On disk it is one line: the card token,
/// optionally followed by <c>since=</c> (unix seconds), <c>streak=</c>,
/// <c>fp=</c> (the adapter fingerprint) and <c>boot=</c> (the boot the verdict
/// was reached in) for an off latch. A bare token written by an earlier build
/// parses as a latch with no timestamp, which is always due for a re-probe.
///
/// The latch is never permanent: the card behind it fails context creation
/// INTERMITTENTLY, so a latch with no expiry converts "lighting works sometimes"
/// into "lighting never works". It is also only as good as the environment it
/// was taken in: a driver update, a card change or a reboot invalidates it.
/// </summary>
internal readonly record struct GpuSelectState(
    string Card, DateTimeOffset? OffSince, int OffStreak, string? Fingerprint = null, string? Boot = null)
{
    public const string Unprobed = "unprobed";
    public const string Integrated = "integrated";
    public const string Discrete = "discrete";
    public const string Off = "off";

    private const long MinUnixSeconds = -62135596800;
    private const long MaxUnixSeconds = 253402300799;

    public static GpuSelectState Fresh => new(Unprobed, null, 0);

    public static GpuSelectState Working(string card) => new(card, null, 0);

    public bool IsOff => Card == Off;

    /// <summary>How long an off latch holds before the card is probed again.
    /// Grows with consecutive latches inside one boot, so a dead card costs one
    /// background probe a day while the box stays up; a reboot starts over.</summary>
    public static TimeSpan ReprobeDelay(int streak, TimeSpan? forced = null)
    {
        if (forced is { } f && f > TimeSpan.Zero)
        {
            return f;
        }
        return streak switch
        {
            <= 1 => TimeSpan.FromHours(1),
            2 => TimeSpan.FromHours(6),
            _ => TimeSpan.FromHours(24),
        };
    }

    public DateTimeOffset? ReprobeAt(TimeSpan? forced = null) =>
        OffSince is { } since ? since + ReprobeDelay(OffStreak, forced) : null;

    /// <summary>How long until the next probe, floored at zero.</summary>
    public TimeSpan ReprobeIn(DateTimeOffset now, TimeSpan? forced = null)
    {
        var at = ReprobeAt(forced);
        if (at is not { } due || due <= now)
        {
            return TimeSpan.Zero;
        }
        return due - now;
    }

    /// <summary>True when the card should be probed again. A missing or future
    /// timestamp counts as due: a clock change must not strand the latch.</summary>
    public bool ReprobeDue(DateTimeOffset now, TimeSpan? forced = null)
    {
        if (!IsOff)
        {
            return true;
        }
        // No timestamp: a latch an earlier build wrote, or one this build is
        // about to stamp. Holding it (rather than probing straight away) is what
        // stops the boot loop the latch exists to stop.
        if (OffSince is not { } since)
        {
            return false;
        }
        return now < since || now - since >= ReprobeDelay(OffStreak, forced);
    }

    /// <summary>Latch off. <paramref name="escalate"/> extends the streak, which
    /// lengthens the next wait; a latch taken on a crash-guard alone must not,
    /// because the guard is left by ANY death inside the init window (a reboot,
    /// an SCM kill) and three of those would otherwise put a healthy card on the
    /// longest wait. A real probe verdict escalates.</summary>
    public GpuSelectState LatchedOff(DateTimeOffset now, bool escalate = true, GpuEnvironment? env = null) =>
        new(Off, now, !escalate ? Math.Max(OffStreak, 1) : IsOff ? OffStreak + 1 : 1, env?.Fingerprint, env?.Boot);

    /// <summary>Give an undated latch a timestamp without extending the streak,
    /// so the re-probe clock starts instead of never running.</summary>
    public GpuSelectState Restamped(DateTimeOffset now, GpuEnvironment? env = null) =>
        new(Off, now, OffStreak <= 0 ? 1 : OffStreak, env?.Fingerprint, env?.Boot);

    /// <summary>True when a dated latch was taken in a different environment
    /// than <paramref name="env"/>, or in one it never recorded: its verdict says
    /// nothing about the card that is present now.</summary>
    public bool LatchStale(GpuEnvironment? env) =>
        IsOff && OffSince is not null && env is { } e && (Fingerprint != e.Fingerprint || Boot != e.Boot);

    public string Format()
    {
        if (!IsOff || OffSince is not { } since)
        {
            return Card;
        }
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{Off} since={since.ToUnixTimeSeconds()} streak={OffStreak}");
        if (Fingerprint is { Length: > 0 } fp)
        {
            line += $" fp={fp}";
        }
        if (Boot is { Length: > 0 } boot)
        {
            line += $" boot={boot}";
        }
        return line;
    }

    public static GpuSelectState Parse(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Fresh;
        }
        var card = parts[0].ToLowerInvariant();
        if (card != Off && card != Integrated && card != Discrete)
        {
            return Fresh;
        }
        DateTimeOffset? since = null;
        var streak = 0;
        string? fingerprint = null;
        string? boot = null;
        for (var i = 1; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = parts[i][..eq];
            var value = parts[i][(eq + 1)..];
            // FromUnixTimeSeconds throws outside DateTimeOffset's range, and an
            // escaping throw here leaves the context neither started nor failed,
            // which reads as "initializing" forever.
            if (key == "since" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                && unix >= MinUnixSeconds && unix <= MaxUnixSeconds)
            {
                since = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            else if (key == "streak" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                streak = n;
            }
            else if (key == "fp" && value.Length > 0)
            {
                fingerprint = value;
            }
            else if (key == "boot" && value.Length > 0)
            {
                boot = value;
            }
        }
        return card == Off ? new(Off, since, streak <= 0 ? 1 : streak, fingerprint, boot) : Working(card);
    }
}

/// <summary>
/// What an off latch is bound to: the usable adapters (device ids and driver
/// version) and the boot session. Both are opaque tokens; equality is all the
/// selection reads from them.
/// </summary>
internal readonly record struct GpuEnvironment(string Fingerprint, string Boot);
