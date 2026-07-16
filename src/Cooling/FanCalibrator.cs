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
    /// <param name="readRpm">Refreshes hardware and returns the fan's RPM.</param>
    public static async Task<FanCalibration> CalibrateOneAsync(
        string fanId,
        Action<int> setDuty,
        Func<int> readRpm,
        IProgress<FanCalibrationProgress>? progress,
        CancellationToken ct)
    {
        var curve = new List<FanCalibrationPoint>(Steps);

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

            var rpm = await MeasureStableRpmAsync(readRpm, ct);
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

    private static async Task<int> MeasureStableRpmAsync(Func<int> readRpm, CancellationToken ct)
    {
        var samples = new Queue<int>();
        var elapsed = 0;

        while (elapsed < MaxWaitMs)
        {
            await Task.Delay(SampleIntervalMs, ct);
            samples.Enqueue(readRpm());
            if (samples.Count > WindowSize) samples.Dequeue();
            elapsed += SampleIntervalMs;

            if (elapsed >= MinSettleMs && samples.Count == WindowSize && FanCalibrationLogic.IsSettled(samples.ToArray()))
                break;
        }

        return samples.Count > 0 ? (int)samples.Average() : 0;
    }
}
