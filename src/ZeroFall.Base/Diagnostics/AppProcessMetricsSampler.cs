using System;
using System.Diagnostics;

namespace ZeroFall.Base.Diagnostics;

public static class AppProcessMetricsSampler
{
    public static (double CpuPercent, long WorkingSetBytes) Sample(
        Process process,
        ref TimeSpan lastCpuTime,
        ref DateTime lastSampleUtc)
    {
        try
        {
            process.Refresh();
        }
        catch
        {
            return (0, 0);
        }

        var workingSet = process.WorkingSet64;
        var now = DateTime.UtcNow;
        var cpuTime = process.TotalProcessorTime;

        var cpuPercent = 0.0;
        if (lastSampleUtc != default)
        {
            var wallMs = (now - lastSampleUtc).TotalMilliseconds;
            var cpuMs = (cpuTime - lastCpuTime).TotalMilliseconds;
            if (wallMs > 0 && Environment.ProcessorCount > 0)
                cpuPercent = cpuMs / wallMs / Environment.ProcessorCount * 100.0;
        }

        lastCpuTime = cpuTime;
        lastSampleUtc = now;
        return (Math.Clamp(cpuPercent, 0, 100), workingSet);
    }

    public static string Format(double cpuPercent, long workingSetBytes) =>
        $"CPU {cpuPercent:F1}% · 内存 {FormatBytes(workingSetBytes)}";

    private static string FormatBytes(long bytes) =>
        bytes < 1024L * 1024
            ? $"{bytes / 1024.0:F0} KB"
            : bytes < 1024L * 1024 * 1024
                ? $"{bytes / (1024.0 * 1024):F0} MB"
                : $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
