namespace Deck.Core.Audio;

/// <summary>
/// Watches for dead air (B5). Fires once when the signal has been below the threshold for the
/// configured time, and again when sound comes back, so the UI can raise and clear an alert.
/// </summary>
public sealed class SilenceDetector
{
    private double _silentSeconds;
    private bool _isSilent;

    public SilenceDetector(float thresholdDb = -50f, double triggerAfterSeconds = 15)
    {
        ThresholdDb = thresholdDb;
        TriggerAfterSeconds = triggerAfterSeconds;
    }

    public float ThresholdDb { get; set; }

    public double TriggerAfterSeconds { get; set; }

    /// <summary>True once silence has lasted longer than <see cref="TriggerAfterSeconds"/>.</summary>
    public bool IsSilent => _isSilent;

    /// <summary>How long the input has been below the threshold, whether or not it has tripped.</summary>
    public double SilentSeconds => _silentSeconds;

    public event EventHandler? SilenceStarted;

    public event EventHandler? SilenceEnded;

    public void Update(float levelDb, double elapsedSeconds)
    {
        if (levelDb < ThresholdDb)
        {
            _silentSeconds += elapsedSeconds;
            if (!_isSilent && _silentSeconds >= TriggerAfterSeconds)
            {
                _isSilent = true;
                SilenceStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _silentSeconds = 0;
            if (_isSilent)
            {
                _isSilent = false;
                SilenceEnded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Reset()
    {
        _silentSeconds = 0;
        _isSilent = false;
    }
}
