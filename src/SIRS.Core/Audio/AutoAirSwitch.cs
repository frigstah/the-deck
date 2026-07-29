namespace Sirs.Core.Audio;

/// <summary>
/// Goes on air when sound appears and off again when it stops (G6), for unattended stations that
/// relay whatever a playout machine is doing.
/// <para>
/// The two delays are deliberately lopsided. Starting is quick, because the first two seconds of a
/// show should not be missing. Stopping is slow - minutes, not seconds - because a gap between
/// tracks, a long pause in an interview and the end of the broadcast all look identical from here,
/// and taking a station off air by mistake is far worse than a few minutes of quiet.
/// </para>
/// <para>
/// This only decides; the caller connects and disconnects. That keeps the decision testable without
/// a server, and keeps the choice of which servers to open in one place.
/// </para>
/// </summary>
public sealed class AutoAirSwitch
{
    private double _soundSeconds;
    private double _silentSeconds;

    public bool Enabled { get; set; }

    /// <summary>Anything above this counts as real sound rather than room noise.</summary>
    public float SignalThresholdDb { get; set; } = -40f;

    public double StartAfterSeconds { get; set; } = 2;

    public double StopAfterSilentSeconds { get; set; } = 300;

    /// <summary>How long the current run of sound or silence has lasted, for the countdown.</summary>
    public double SoundSeconds => _soundSeconds;

    public double SilentSeconds => _silentSeconds;

    public event EventHandler? StartRequested;

    public event EventHandler? StopRequested;

    /// <summary>
    /// Called regularly with the current level. <paramref name="isBroadcasting"/> stops it asking
    /// for something that has already happened.
    /// </summary>
    public void Update(float levelDb, bool isBroadcasting, double elapsedSeconds)
    {
        if (!Enabled)
        {
            Reset();
            return;
        }

        if (elapsedSeconds <= 0) return;

        if (levelDb >= SignalThresholdDb)
        {
            _silentSeconds = 0;
            _soundSeconds += elapsedSeconds;

            if (!isBroadcasting && _soundSeconds >= StartAfterSeconds)
            {
                _soundSeconds = 0;
                StartRequested?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        _soundSeconds = 0;
        _silentSeconds += elapsedSeconds;

        if (isBroadcasting && _silentSeconds >= StopAfterSilentSeconds)
        {
            _silentSeconds = 0;
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What it is waiting for, in words, or null when there is nothing to say.</summary>
    public string? Status(bool isBroadcasting)
    {
        if (!Enabled) return null;

        if (!isBroadcasting)
        {
            return _soundSeconds > 0
                ? $"Sound detected — going live in {Math.Max(0, StartAfterSeconds - _soundSeconds):0} s."
                : "Waiting for sound. SIRS will go live on its own when it hears something.";
        }

        if (_silentSeconds < 10) return "Will come off air on its own after a long silence.";

        var remaining = Math.Max(0, StopAfterSilentSeconds - _silentSeconds);
        return $"Quiet for {_silentSeconds:0} s — coming off air in {remaining / 60:0}m {remaining % 60:0}s unless sound returns.";
    }

    public void Reset()
    {
        _soundSeconds = 0;
        _silentSeconds = 0;
    }
}
