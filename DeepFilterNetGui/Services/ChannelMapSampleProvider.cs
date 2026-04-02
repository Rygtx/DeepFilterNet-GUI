using NAudio.Wave;

namespace DeepFilterNetGui.Services;

internal sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private readonly int _outputChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public ChannelMapSampleProvider(ISampleProvider source, int outputChannels)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceChannels = source.WaveFormat.Channels;
        _outputChannels = Math.Clamp(outputChannels, 1, 2);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0)
            return 0;

        int framesRequested = count / _outputChannels;
        if (framesRequested <= 0)
            return 0;

        int sourceSamplesRequested = framesRequested * _sourceChannels;
        EnsureSourceCapacity(sourceSamplesRequested);

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesRequested);
        int sourceFramesRead = sourceSamplesRead / _sourceChannels;
        int outputSamplesWritten = sourceFramesRead * _outputChannels;

        if (outputSamplesWritten > 0)
        {
            MapChannels(_sourceBuffer, sourceFramesRead, _sourceChannels, buffer, offset, _outputChannels);
        }

        if (outputSamplesWritten < count)
        {
            Array.Clear(buffer, offset + outputSamplesWritten, count - outputSamplesWritten);
        }

        return count;
    }

    private void EnsureSourceCapacity(int samples)
    {
        if (_sourceBuffer.Length < samples)
        {
            Array.Resize(ref _sourceBuffer, samples);
        }
    }

    public static void MapChannels(float[] source, int frames, int sourceChannels, float[] destination, int destinationOffset, int destinationChannels)
    {
        if (frames <= 0)
            return;

        Array.Clear(destination, destinationOffset, frames * destinationChannels);

        if (sourceChannels <= 1 && destinationChannels == 1)
        {
            Array.Copy(source, 0, destination, destinationOffset, frames);
            return;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int sourceBase = frame * sourceChannels;
            int destinationBase = destinationOffset + frame * destinationChannels;

            if (destinationChannels == 1)
            {
                if (sourceChannels <= 1)
                {
                    destination[destinationBase] = source[sourceBase];
                }
                else
                {
                    destination[destinationBase] = 0.5f * (source[sourceBase] + source[sourceBase + 1]);
                }
            }
            else
            {
                if (sourceChannels <= 1)
                {
                    float mono = source[sourceBase];
                    destination[destinationBase] = mono;
                    destination[destinationBase + 1] = mono;
                }
                else
                {
                    destination[destinationBase] = source[sourceBase];
                    destination[destinationBase + 1] = source[sourceBase + 1];
                }
            }
        }
    }
}
