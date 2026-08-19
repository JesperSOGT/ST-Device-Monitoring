namespace ST_Device_Monitoring.Core;

/// <summary>
/// Rolling statistics over the last N seconds, stored as one bucket per second.
/// O(1) per check, O(N) when the UI reads it - so a new fault is visible immediately
/// instead of being diluted by 24 hours of totals.
/// </summary>
public sealed class RollingWindow
{
    private readonly int _seconds;
    private readonly long[] _bucketSecond;
    private readonly int[] _sent;
    private readonly int[] _failed;
    private readonly long[] _rttSum;
    private readonly int[] _rttCount;
    private readonly long[] _jitterSum;
    private readonly int[] _jitterCount;
    private readonly object _lock = new();

    private long _previousRtt = -1;

    public RollingWindow(int seconds = 60)
    {
        _seconds = seconds;
        _bucketSecond = new long[seconds];
        _sent = new int[seconds];
        _failed = new int[seconds];
        _rttSum = new long[seconds];
        _rttCount = new int[seconds];
        _jitterSum = new long[seconds];
        _jitterCount = new int[seconds];
        for (int i = 0; i < seconds; i++) _bucketSecond[i] = long.MinValue;
    }

    public int WindowSeconds => _seconds;

    public void Add(DateTime timestamp, bool success, long rtt)
    {
        var second = timestamp.Ticks / TimeSpan.TicksPerSecond;
        var index = (int)(second % _seconds);

        lock (_lock)
        {
            if (_bucketSecond[index] != second)
            {
                _bucketSecond[index] = second;
                _sent[index] = 0;
                _failed[index] = 0;
                _rttSum[index] = 0;
                _rttCount[index] = 0;
                _jitterSum[index] = 0;
                _jitterCount[index] = 0;
            }

            _sent[index]++;
            if (!success)
            {
                _failed[index]++;
                _previousRtt = -1; // a gap breaks the jitter chain
                return;
            }

            _rttSum[index] += rtt;
            _rttCount[index]++;

            if (_previousRtt >= 0)
            {
                _jitterSum[index] += Math.Abs(rtt - _previousRtt);
                _jitterCount[index]++;
            }
            _previousRtt = rtt;
        }
    }

    public RollingStats GetStats(DateTime now)
    {
        var newest = now.Ticks / TimeSpan.TicksPerSecond;
        var oldest = newest - _seconds + 1;

        long sent = 0, failed = 0, rttSum = 0, jitterSum = 0;
        int rttCount = 0, jitterCount = 0;

        lock (_lock)
        {
            for (int i = 0; i < _seconds; i++)
            {
                var second = _bucketSecond[i];
                if (second < oldest || second > newest) continue;
                sent += _sent[i];
                failed += _failed[i];
                rttSum += _rttSum[i];
                rttCount += _rttCount[i];
                jitterSum += _jitterSum[i];
                jitterCount += _jitterCount[i];
            }
        }

        return new RollingStats
        {
            Seconds = _seconds,
            Sent = sent,
            Failed = failed,
            AvgRtt = rttCount == 0 ? 0 : (double)rttSum / rttCount,
            AvgJitter = jitterCount == 0 ? 0 : (double)jitterSum / jitterCount
        };
    }

    public void Reset()
    {
        lock (_lock)
        {
            for (int i = 0; i < _seconds; i++) _bucketSecond[i] = long.MinValue;
            _previousRtt = -1;
        }
    }
}

public readonly struct RollingStats
{
    public int Seconds { get; init; }
    public long Sent { get; init; }
    public long Failed { get; init; }
    public double AvgRtt { get; init; }
    public double AvgJitter { get; init; }

    public double LossPercent => Sent == 0 ? 0 : Failed * 100.0 / Sent;
}
