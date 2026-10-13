using System.Diagnostics;

namespace DackelFxp.Ftp.Transfer;

/// <summary>
/// Token bucket for transfer speed limits. Limiters can be chained (e.g. per site and global):
/// every transfer waits for its own limiter and all parents.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private long _lastTicks;

    public BandwidthLimiter(long bytesPerSecond = 0, BandwidthLimiter? parent = null)
    {
        BytesPerSecond = bytesPerSecond;
        Parent = parent;
    }

    /// <summary>0 = unlimited.</summary>
    public long BytesPerSecond { get; set; }
    public BandwidthLimiter? Parent { get; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        for (var limiter = this; limiter is not null; limiter = limiter.Parent)
            await limiter.WaitSelfAsync(bytes, ct).ConfigureAwait(false);
    }

    private async ValueTask WaitSelfAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var rate = BytesPerSecond;
                if (rate <= 0) return;

                var now = _clock.ElapsedTicks;
                _tokens = Math.Min(rate, _tokens + (now - _lastTicks) * rate / (double)Stopwatch.Frequency);
                _lastTicks = now;
                if (_tokens >= bytes || _tokens >= rate)
                {
                    _tokens -= bytes;
                    return;
                }
                delay = TimeSpan.FromSeconds((bytes - _tokens) / rate);
            }
            await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay, ct).ConfigureAwait(false);
        }
    }
}
