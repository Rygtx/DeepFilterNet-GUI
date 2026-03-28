namespace DeepFilterNetGui.Services;

internal readonly record struct TimingSnapshot(double TotalMs, long Calls, long Samples)
{
    public double AvgSamplesPerCall => Calls > 0 ? (double)Samples / Calls : 0;
}

internal sealed class TimingAccumulator
{
    private readonly object _lockObj = new();
    private double _totalMs;
    private long _calls;
    private long _samples;

    public void Add(double elapsedMs, int samples)
    {
        lock (_lockObj)
        {
            _totalMs += elapsedMs;
            _calls++;
            _samples += samples;
        }
    }

    public TimingSnapshot SnapshotAndReset()
    {
        lock (_lockObj)
        {
            var snapshot = new TimingSnapshot(_totalMs, _calls, _samples);
            _totalMs = 0;
            _calls = 0;
            _samples = 0;
            return snapshot;
        }
    }
}

internal sealed class TimingSampleProvider : NAudio.Wave.ISampleProvider
{
    private readonly NAudio.Wave.ISampleProvider _inner;
    private readonly TimingAccumulator _accumulator;

    public TimingSampleProvider(NAudio.Wave.ISampleProvider inner, TimingAccumulator accumulator)
    {
        _inner = inner;
        _accumulator = accumulator;
        WaveFormat = inner.WaveFormat;
    }

    public NAudio.Wave.WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int read = _inner.Read(buffer, offset, count);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _accumulator.Add(ms, read);
        return read;
    }
}

