using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// Probes a fan's PWM→RPM response by ramping duty from 100% to 0% in 10%
/// steps, waiting for the RPM to stabilize at each level, then classifying
/// the fan from the resulting curve.
///
/// Pure algorithm: the caller supplies the duty writer and the RPM reader, so
/// this holds no hardware handles and never restores fan state. The provider
/// owns the calibration lease and every write that reaches hardware - see
/// WindowsFanControlProvider.BeginCalibrationLease.
/// </summary>
public static class FanCalibrator
{
    private const int Steps = 11;
    private const int StepSize = 10;
    private const int SampleIntervalMs = 250;
    private const int MinSettleMs = 1500;
    private const int MaxWaitMs = 12000;
    private const int WindowSize = 6;

    /// <param name="setDuty">Commands a duty 0-100. Silently stops driving once
    /// the provider revokes the calibration lease, which is how a shutdown
    /// mid-ramp leaves fans on the state ReleaseAll just set.</param>
    /// <param name="readRpm">Refreshes hardware and returns the fan's RPM, or
    /// null when the tach could not be read. A null is a failed sample, never 0.</param>
    /// <param name="sampleDelayMs">Real wait between reads. The settle timing stays
    /// in sample counts, so a test can pass a tiny value without changing the logic.</param>
    /// <param name="priorRpm">The fan's RPM before the sweep took control.</param>
    /// <exception cref="CalibrationRejectedException">The fan spun before the sweep
    /// but the sweep never saw it spin, so the readings are not trustworthy.</exception>
    public static async Task<FanCalibration> CalibrateOneAsync(
        string fanId,
        Action<int> setDuty,
        Func<int?> readRpm,
        int priorRpm,
        IProgress<FanCalibrationProgress>? progress,
        CancellationToken ct,
        int sampleDelayMs = SampleIntervalMs)
    {
        var curve = new List<FanCalibrationPoint>(Steps);
        var failedSamples = 0;
        var totalSamples = 0;

        for (int i = 0; i < Steps; i++)
        {
            ct.ThrowIfCancellationRequested();

            var duty = 100 - i * StepSize;
            setDuty(duty);

            progress?.Report(new FanCalibrationProgress
            {
                FanId = fanId,
                CurrentDuty = duty,
                StepIndex = i,
                TotalSteps = Steps,
                State = "Settling",
            });

            var (measured, failed, total) = await MeasureStableRpmAsync(readRpm, sampleDelayMs, ct);
            failedSamples += failed;
            totalSamples += total;
            var rpm = measured ?? 0;
            curve.Add(new FanCalibrationPoint { Duty = duty, Rpm = rpm });

            progress?.Report(new FanCalibrationProgress
            {
                FanId = fanId,
                CurrentDuty = duty,
                CurrentRpm = rpm,
                StepIndex = i,
                TotalSteps = Steps,
                State = "Recording",
            });
        }

        var result = FanCalibrationLogic.Classify(fanId, curve);

        if (FanCalibrationLogic.IsImplausibleSweep(priorRpm, result.MaxRpm, failedSamples, totalSamples))
        {
            progress?.Report(new FanCalibrationProgress
            {
                FanId = fanId,
                CurrentRpm = result.MaxRpm,
                StepIndex = Steps - 1,
                TotalSteps = Steps,
                State = "Failed",
            });
            throw new CalibrationRejectedException(fanId, priorRpm, result.MaxRpm, failedSamples, totalSamples);
        }

        progress?.Report(new FanCalibrationProgress
        {
            FanId = fanId,
            CurrentRpm = result.MaxRpm,
            StepIndex = Steps - 1,
            TotalSteps = Steps,
            State = "Done",
        });

        return result;
    }

    private static async Task<(int? Rpm, int Failed, int Total)> MeasureStableRpmAsync(Func<int?> readRpm, int sampleDelayMs, CancellationToken ct)
    {
        var samples = new Queue<int>();
        var failed = 0;
        var total = 0;
        var elapsed = 0;

        while (elapsed < MaxWaitMs)
        {
            await Task.Delay(sampleDelayMs, ct);
            total++;
            if (readRpm() is { } rpm)
            {
                samples.Enqueue(rpm);
                if (samples.Count > WindowSize) samples.Dequeue();
            }
            else
            {
                failed++;
            }
            elapsed += SampleIntervalMs;

            if (elapsed >= MinSettleMs && samples.Count == WindowSize && FanCalibrationLogic.IsSettled(samples.ToArray()))
                break;
        }

        return (samples.Count > 0 ? (int)samples.Average() : null, failed, total);
    }
}

public sealed class CalibrationRejectedException(string fanId, int priorRpm, int maxRpm, int failedSamples, int totalSamples)
    : Exception($"{fanId} read {priorRpm} RPM before the sweep but at most {maxRpm} RPM during it ({failedSamples} of {totalSamples} reads failed)")
{
    public string FanId { get; } = fanId;
    public int PriorRpm { get; } = priorRpm;
    public int MaxRpm { get; } = maxRpm;
    public int FailedSamples { get; } = failedSamples;
    public int TotalSamples { get; } = totalSamples;
}
