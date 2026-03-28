using NAudio.Dsp;

namespace DeepFilterNetGui.Audio;

public sealed class StftProcessor
{
    private readonly int _fftSize;
    private readonly int _hopSize;
    private readonly int _fftM;
    private readonly float[] _window;
    private readonly float[] _windowSq;
    private readonly float[] _inputBuffer;
    private readonly float[] _olaBuffer;
    private readonly float[] _olaNorm;
    private readonly Complex[] _fftBuffer;

    public StftProcessor(int fftSize = 512, int hopSize = 256)
    {
        _fftSize = fftSize;
        _hopSize = hopSize;
        _fftM = (int)Math.Log2(fftSize);
        _window = new float[fftSize];
        _windowSq = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            var w = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (fftSize - 1));
            _window[i] = w;
            _windowSq[i] = w * w;
        }

        _inputBuffer = new float[fftSize];
        _olaBuffer = new float[fftSize];
        _olaNorm = new float[fftSize];
        _fftBuffer = new Complex[fftSize];
    }

    public int FftSize => _fftSize;
    public int HopSize => _hopSize;

    public void AnalyzeTo(float[] hopSamples, float[] specOut)
    {
        if (hopSamples.Length != _hopSize)
            throw new ArgumentException($"hopSamples length must be {_hopSize}.");
        if (specOut.Length != (_fftSize / 2 + 1) * 2)
            throw new ArgumentException("specOut length mismatch.");

        Array.Copy(_inputBuffer, _hopSize, _inputBuffer, 0, _fftSize - _hopSize);
        Array.Copy(hopSamples, 0, _inputBuffer, _fftSize - _hopSize, _hopSize);

        for (int i = 0; i < _fftSize; i++)
        {
            _fftBuffer[i].X = _inputBuffer[i] * _window[i];
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, _fftM, _fftBuffer);

        int bins = _fftSize / 2 + 1;
        for (int i = 0; i < bins; i++)
        {
            specOut[i * 2] = _fftBuffer[i].X;
            specOut[i * 2 + 1] = _fftBuffer[i].Y;
        }
    }

    public void SynthesizeFrom(float[] specIn, float[] hopOut)
    {
        if (specIn.Length != (_fftSize / 2 + 1) * 2)
            throw new ArgumentException("specIn length mismatch.");
        if (hopOut.Length != _hopSize)
            throw new ArgumentException($"hopOut length must be {_hopSize}.");

        int bins = _fftSize / 2 + 1;
        for (int i = 0; i < bins; i++)
        {
            _fftBuffer[i].X = specIn[i * 2];
            _fftBuffer[i].Y = specIn[i * 2 + 1];
        }

        for (int i = 1; i < bins - 1; i++)
        {
            _fftBuffer[_fftSize - i].X = _fftBuffer[i].X;
            _fftBuffer[_fftSize - i].Y = -_fftBuffer[i].Y;
        }

        FastFourierTransform.FFT(false, _fftM, _fftBuffer);

        float scale = 1f;
        for (int i = 0; i < _fftSize; i++)
        {
            float sample = _fftBuffer[i].X * scale;
            _olaBuffer[i] += sample * _window[i];
            _olaNorm[i] += _windowSq[i];
        }

        for (int i = 0; i < _hopSize; i++)
        {
            float norm = _olaNorm[i];
            hopOut[i] = norm > 1e-8f ? _olaBuffer[i] / norm : 0f;
        }

        Array.Copy(_olaBuffer, _hopSize, _olaBuffer, 0, _fftSize - _hopSize);
        Array.Clear(_olaBuffer, _fftSize - _hopSize, _hopSize);
        Array.Copy(_olaNorm, _hopSize, _olaNorm, 0, _fftSize - _hopSize);
        Array.Clear(_olaNorm, _fftSize - _hopSize, _hopSize);
    }

    public void Reset()
    {
        Array.Clear(_inputBuffer, 0, _inputBuffer.Length);
        Array.Clear(_olaBuffer, 0, _olaBuffer.Length);
        Array.Clear(_olaNorm, 0, _olaNorm.Length);
    }
}

