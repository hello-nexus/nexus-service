using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// Pure classification and stability detection logic for fan calibration.
/// Platform-independent, no LHM dependency. Used by FanCalibrator (Windows)
/// and tests.
/// </summary>
public static class FanCalibrationLogic
{
    /// <summary>At or below this the fan is not reporting a usable tach.</summary>
    private const int UnresponsiveMaxRpm = 100;
    /// <summary>Tach jitter tolerated before a flat response counts as a spread.</summary>
    private const int FixedSpreadFloorRpm = 50;
    /// <summary>Spread within this fraction of max RPM is a flat response. The
    /// relative term keeps a high-RPM pump, whose usable spread is a few percent
    /// of its range, out of Fixed - which strips its duty control.</summary>
    private const double FixedSpreadFraction = 0.03;

    public static bool IsStable(IEnumerable<int> samples)
    {
        var arr = samples.Select(s => (double)s).ToArray();
        if (arr.Length == 0)
        {
            return true;
        }

        var mean = arr.Average();
        var variance = arr.Select(x => (x - mean) * (x - mean)).Sum() / arr.Length;
        var stddev = Math.Sqrt(variance);
        var tolerance = Math.Max(20.0, mean * 0.03);
        return stddev < tolerance;
    }

    /// <summary>
    /// A slow monotonic ramp has low variance over a short window while still
    /// far from terminal RPM, so IsStable alone latches mid-ramp. This also
    /// requires the first-half and second-half window means to agree, which
    /// rejects a trending window and waits for the true plateau.
    /// </summary>
    public static bool IsSettled(IReadOnlyList<int> samples)
    {
        if (!IsStable(samples))
        {
            return false;
        }

        if (samples.Count < 2)
        {
            return true;
        }

        var half = samples.Count / 2;
        var firstMean = samples.Take(half).Average();
        var secondMean = samples.Skip(samples.Count - half).Average();
        var mean = samples.Average();
        var tol = Math.Max(15.0, mean * 0.02);
        return Math.Abs(secondMean - firstMean) <= tol;
    }

    public static FanCalibration Classify(string fanId, List<FanCalibrationPoint> curve)
    {
        var rpms = curve.Select(p => p.Rpm).ToList();
        var maxRpm = rpms.Count > 0 ? rpms.Max() : 0;
        // The true floor - 0 for a fan that stops at the bottom of the sweep.
        var minRpmAll = rpms.Count > 0 ? rpms.Min() : 0;
        var nonZero = curve.Where(p => p.Rpm > 0).ToList();
        // The lowest duty that still spun on a DESCENDING ramp: the fan's
        // sustain floor, not the duty needed to start it from rest, which is
        // higher. Do not use this as a floor for driving a stopped fan.
        var minDuty = nonZero.Count > 0 ? nonZero.Min(p => p.Duty) : 0;

        // Stopping at 0% duty is correct behaviour, so the stall test ignores
        // that endpoint: Stalling means the fan drops out inside the band the
        // user can select.
        var stallsInBand = curve.Any(p => p.Duty > 0 && p.Rpm == 0);
        var spreadGate = Math.Max(FixedSpreadFloorRpm, maxRpm * FixedSpreadFraction);

        string classification;
        if (maxRpm <= UnresponsiveMaxRpm)
        {
            classification = "Unresponsive";
        }
        else if (stallsInBand)
        {
            classification = "Stalling";
        }
        else if (maxRpm - minRpmAll <= spreadGate)
        {
            classification = "Fixed";
        }
        else
        {
            classification = "Controllable";
        }

        return new FanCalibration
        {
            FanId = fanId,
            Classification = classification,
            MinRpm = minRpmAll,
            MaxRpm = maxRpm,
            MinDuty = minDuty,
            Curve = curve,
            CalibratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
}
