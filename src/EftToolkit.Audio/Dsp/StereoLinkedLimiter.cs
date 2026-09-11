using EftToolkit.Core.Configuration;

namespace EftToolkit.Audio.Dsp;

/// <summary>
/// Gain and a look-ahead peak limiter for interleaved stereo audio, with one detector and one gain
/// envelope shared by both channels.
/// </summary>
/// <remarks>
/// <para>
/// The two channels are linked deliberately. Reducing each channel by whatever it alone needs would
/// pull the stereo image apart every time one side was louder, which on game audio means the
/// phantom centre wanders. A single envelope keeps the relationship between the channels exactly as
/// it was recorded.
/// </para>
/// <para>
/// The detector reads the incoming frame while the gain is applied to a frame taken from the delay
/// line, so the gain has already moved by the time a transient arrives at the output. This is what
/// keeps a fast attack from clipping the first sample of a peak.
/// </para>
/// <para>
/// Not thread safe: one thread processes blocks, and any thread may read <see cref="Metrics"/>.
/// </para>
/// </remarks>
public sealed class StereoLinkedLimiter
{
    private int _sampleRate;
    private AudioLimiterOptions _options = null!;

    private double _inputGainLinear;
    private double _thresholdDb;
    private double _ratio;
    private double _kneeDb;
    private double _ceilingLinear;
    private double _attackCoefficient;
    private double _releaseCoefficient;

    /// <summary>Stereo delay line holding one frame per slot. Empty when there is no look-ahead.</summary>
    private float[] _delayLine = [];

    private int _delayFrames;
    private int _delayIndex;

    /// <summary>Current gain, never above 1.0. One value for both channels.</summary>
    private double _envelope = 1.0;

    private double _inputPeakDbFs = AudioMath.LinearToDb(AudioMath.Silence);
    private double _outputPeakDbFs = AudioMath.LinearToDb(AudioMath.Silence);
    private double _gainReductionDb;

    public StereoLinkedLimiter(int sampleRate, AudioLimiterOptions options)
    {
        Reconfigure(sampleRate, options);
    }

    /// <summary>
    /// What the limiter did to the last processed block. Gain reduction is the deepest reduction
    /// reached within that block, so it is a peak reading rather than an instant one.
    /// </summary>
    public AudioProcessorMetrics Metrics => new(
        Volatile.Read(ref _inputPeakDbFs),
        Volatile.Read(ref _outputPeakDbFs),
        Volatile.Read(ref _gainReductionDb));

    /// <summary>The delay the look-ahead introduces, in frames.</summary>
    public int LookAheadFrames => _delayFrames;

    /// <summary>
    /// Applies gain and limiting to interleaved stereo frames in place. The span length must be
    /// even, because a single unpaired sample is not a frame.
    /// </summary>
    public void Process(Span<float> interleavedStereo)
    {
        if (interleavedStereo.Length % 2 != 0)
        {
            throw new ArgumentException(
                "Interleaved stereo audio must contain a whole number of two-channel frames.",
                nameof(interleavedStereo));
        }

        if (interleavedStereo.Length == 0)
        {
            return;
        }

        double inputPeak = AudioMath.Silence;
        double outputPeak = AudioMath.Silence;
        double minimumEnvelope = 1.0;

        for (int offset = 0; offset < interleavedStereo.Length; offset += 2)
        {
            // A NaN or an infinity from the capture side must not be multiplied by the envelope and
            // then stored in the delay line, where it would keep reappearing.
            float left = AudioMath.Sanitize(interleavedStereo[offset]) * (float)_inputGainLinear;
            float right = AudioMath.Sanitize(interleavedStereo[offset + 1]) * (float)_inputGainLinear;

            double peak = Math.Max(Math.Abs(left), Math.Abs(right));

            if (peak > inputPeak)
            {
                inputPeak = peak;
            }

            _envelope = AdvanceEnvelope(_envelope, TargetGainFor(peak));

            if (_envelope < minimumEnvelope)
            {
                minimumEnvelope = _envelope;
            }

            (float delayedLeft, float delayedRight) = Delay(left, right);

            float outLeft = (float)(delayedLeft * _envelope);
            float outRight = (float)(delayedRight * _envelope);

            // The ceiling is a hard backstop. The soft knee should have brought the signal under it
            // already; this is what guarantees it whatever the detector was handed.
            outLeft = (float)Math.Clamp(outLeft, -_ceilingLinear, _ceilingLinear);
            outRight = (float)Math.Clamp(outRight, -_ceilingLinear, _ceilingLinear);

            double output = Math.Max(Math.Abs(outLeft), Math.Abs(outRight));

            if (output > outputPeak)
            {
                outputPeak = output;
            }

            interleavedStereo[offset] = outLeft;
            interleavedStereo[offset + 1] = outRight;
        }

        Volatile.Write(ref _inputPeakDbFs, AudioMath.LinearToDbSafe(inputPeak));
        Volatile.Write(ref _outputPeakDbFs, AudioMath.LinearToDbSafe(outputPeak));

        // Reported as an attenuation, so a limiter that is working shows a positive number.
        Volatile.Write(
            ref _gainReductionDb,
            minimumEnvelope >= 1.0 ? 0.0 : -AudioMath.LinearToDb(minimumEnvelope));
    }

    /// <summary>
    /// Replaces the configuration. The look-ahead line is reallocated here rather than during
    /// processing, and the gain returns to unity because the old envelope measured a different
    /// signal path.
    /// </summary>
    public void Reconfigure(int sampleRate, AudioLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "A sample rate must be positive.");
        }

        _sampleRate = sampleRate;
        _options = options;

        _inputGainLinear = AudioMath.DbToLinear(options.InputGainDb);
        _thresholdDb = options.ThresholdDbFs;
        _ratio = Math.Max(options.Ratio, 1.0);
        _kneeDb = Math.Max(options.KneeDb, 0.0);
        _ceilingLinear = Math.Min(AudioMath.DbToLinear(options.CeilingDbFs), 1.0);

        _attackCoefficient = CoefficientFor(options.AttackMs, sampleRate);
        _releaseCoefficient = CoefficientFor(options.ReleaseMs, sampleRate);

        _delayFrames = (int)Math.Round(sampleRate * Math.Max(options.LookAheadMs, 0.0) / 1000.0, MidpointRounding.AwayFromZero);
        _delayLine = _delayFrames > 0 ? new float[_delayFrames * 2] : [];
        _delayIndex = 0;

        _envelope = 1.0;
    }

    /// <summary>Empties the delay line and returns the gain to unity, as if the limiter were new.</summary>
    public void Reset()
    {
        Array.Clear(_delayLine);
        _delayIndex = 0;
        _envelope = 1.0;

        Volatile.Write(ref _inputPeakDbFs, AudioMath.LinearToDb(AudioMath.Silence));
        Volatile.Write(ref _outputPeakDbFs, AudioMath.LinearToDb(AudioMath.Silence));
        Volatile.Write(ref _gainReductionDb, 0.0);
    }

    /// <summary>
    /// How fast the gain moves toward its target, as a one-pole coefficient. A zero or negative
    /// time means the gain moves immediately, which is also what keeps the expression finite.
    /// </summary>
    private static double CoefficientFor(double milliseconds, int sampleRate)
    {
        if (milliseconds <= 0.0)
        {
            return 0.0;
        }

        return Math.Exp(-1.0 / (milliseconds / 1000.0 * sampleRate));
    }

    /// <summary>
    /// The gain the current frame calls for, before smoothing. Above the knee the output is
    /// compressed toward the threshold at the configured ratio; inside the knee the correction is
    /// quadratic so the transition into compression has no corner.
    /// </summary>
    private double TargetGainFor(double peak)
    {
        double levelDb = AudioMath.LinearToDbSafe(peak);
        double over = levelDb - _thresholdDb;

        if (over <= -_kneeDb / 2.0)
        {
            return 1.0;
        }

        double gainDb;

        if (_kneeDb <= 0.0)
        {
            // A hard knee: no quadratic region exists, and the formula for one divides by zero.
            gainDb = _thresholdDb + (over / _ratio) - levelDb;
        }
        else if (over >= _kneeDb / 2.0)
        {
            gainDb = _thresholdDb + (over / _ratio) - levelDb;
        }
        else
        {
            gainDb = ((1.0 / _ratio) - 1.0) * Math.Pow(over + (_kneeDb / 2.0), 2.0) / (2.0 * _kneeDb);
        }

        return Math.Min(1.0, AudioMath.DbToLinear(gainDb));
    }

    /// <summary>
    /// Moves the envelope toward the target, attacking when the gain has to come down and releasing
    /// when it may go back up.
    /// </summary>
    private double AdvanceEnvelope(double envelope, double target)
    {
        double coefficient = target < envelope ? _attackCoefficient : _releaseCoefficient;

        return (coefficient * (envelope - target)) + target;
    }

    /// <summary>
    /// Writes the current frame into the look-ahead line and returns the frame from the far end.
    /// With no look-ahead configured the frame comes straight back.
    /// </summary>
    private (float Left, float Right) Delay(float left, float right)
    {
        if (_delayFrames == 0)
        {
            return (left, right);
        }

        int slot = _delayIndex * 2;
        float delayedLeft = _delayLine[slot];
        float delayedRight = _delayLine[slot + 1];

        _delayLine[slot] = left;
        _delayLine[slot + 1] = right;

        _delayIndex++;

        if (_delayIndex >= _delayFrames)
        {
            _delayIndex = 0;
        }

        return (delayedLeft, delayedRight);
    }
}
