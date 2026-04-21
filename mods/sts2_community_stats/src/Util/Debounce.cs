using System.Diagnostics;

namespace CommunityStats.Util;

/// <summary>
/// High-precision debounce gate. Returns true at most once per interval.
/// Uses Stopwatch for sub-millisecond accuracy (no DateTime GC allocations).
/// </summary>
public class Debounce
{
    private long _lastTick;
    private readonly long _minIntervalTicks;

    public Debounce(int minIntervalMs)
    {
        _minIntervalTicks = minIntervalMs * (Stopwatch.Frequency / 1000);
        _lastTick = 0;
    }

    /// <summary>
    /// Returns true if enough time has elapsed since the last successful call.
    /// </summary>
    public bool CanFire()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastTick < _minIntervalTicks) return false;
        _lastTick = now;
        return true;
    }

    /// <summary>
    /// Reset the debounce timer (next CanFire() will return true).
    /// </summary>
    public void Reset() => _lastTick = 0;
}
