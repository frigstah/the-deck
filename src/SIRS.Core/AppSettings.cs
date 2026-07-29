using System.Text.Json;
using System.Text.Json.Serialization;
using Sirs.Core.Audio;
using Sirs.Core.Audio.Dsp;
using Sirs.Core.Control;
using Sirs.Core.Metadata;
using Sirs.Core.Recording;
using Sirs.Core.Servers;

namespace Sirs.Core;

/// <summary>
/// Which palette SIRS draws with (I5). Stored by name, so the settings file stays readable and a
/// value that is no longer recognised falls back to following Windows rather than to nothing.
/// </summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Everything SIRS remembers between runs that is not a server profile.</summary>
public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }

    /// <summary>
    /// Whether the saved input is a real input or a loopback source. Stored alongside the id
    /// because the same endpoint can appear as both an output and a loopback capture source.
    /// </summary>
    public AudioDeviceKind InputDeviceKind { get; set; } = AudioDeviceKind.Input;

    public string? MonitorDeviceId { get; set; }

    public float InputGainDb { get; set; }

    // Which of the device's inputs are used (A7).
    public int InputFirstChannel { get; set; }

    public bool InputSingleChannel { get; set; }

    // Second mixer source (A5). Defaults to a loopback source, since "music under my voice" is
    // what the second fader is overwhelmingly used for.
    public bool SecondaryEnabled { get; set; }

    public string? SecondaryDeviceId { get; set; }

    public AudioDeviceKind SecondaryDeviceKind { get; set; } = AudioDeviceKind.Loopback;

    public float SecondaryGainDb { get; set; } = -6f;

    public bool VoiceEnhance { get; set; }

    /// <summary>Automatic gain control on the main input (E3).</summary>
    public bool AutoGain { get; set; }

    /// <summary>Minutes per recording file; zero means one file per show (G3).</summary>
    public int RecordingSplitMinutes { get; set; }

    public bool MonitorEnabled { get; set; }

    public float MonitorVolume { get; set; } = 0.8f;

    public Guid? SelectedServerId { get; set; }

    /// <summary>
    /// Extra destinations the show also goes to (C12) - a backup relay, or the same show at another
    /// bitrate. An id with no matching server is skipped rather than treated as an error: server
    /// lists get edited and shared, and a stale entry here should never block going on air.
    /// </summary>
    public List<Guid> AlsoSendToServerIds { get; set; } = [];

    public bool RecordWhileBroadcasting { get; set; }

    public string RecordingFolder { get; set; } = AppPaths.DefaultRecordingDirectory;

    public string RecordingFilenameTemplate { get; set; } = "{station} {date} {time}";

    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.SameAsStream;

    // ---- now playing (F4, F5)

    /// <summary>How the pieces of a track are combined into the line listeners see.</summary>
    public string TitleTemplate { get; set; } = Metadata.TitleTemplate.Default;

    /// <summary>Whether the local endpoint automation systems push titles to is listening.</summary>
    public bool MetadataEndpointEnabled { get; set; }

    public int MetadataPort { get; set; } = MetadataServer.DefaultPort;

    /// <summary>Off means loopback only, which is what almost everyone wants.</summary>
    public bool MetadataAllowOtherComputers { get; set; }

    /// <summary>
    /// Password for the endpoint, protected the same way server passwords are. Required before the
    /// endpoint will accept anything from outside this computer.
    /// </summary>
    [JsonPropertyName("metadataToken")]
    public string? ProtectedMetadataToken { get; set; }

    [JsonIgnore]
    public string? MetadataToken
    {
        get => SecretProtector.Unprotect(ProtectedMetadataToken);
        set => ProtectedMetadataToken = SecretProtector.Protect(value);
    }

    /// <summary>Whether the spectrum and phase panel is open (B9). Closed by default.</summary>
    public bool ShowAdvancedMeters { get; set; }

    /// <summary>
    /// Which pane of the window was open last. Remembered because someone who spends a show on the
    /// sound pane should not be put back on the servers pane every time SIRS starts.
    /// </summary>
    public int SelectedSection { get; set; }

    // ---- remote control (I10)

    /// <summary>
    /// Whether other programs can drive SIRS over HTTP. Off by default and deliberately separate
    /// from the now-playing endpoint: one changes what listeners read, the other can put a station
    /// on air, and someone who wants the first should not silently get the second.
    /// </summary>
    public bool ControlEndpointEnabled { get; set; }

    public int ControlPort { get; set; } = ControlServer.DefaultPort;

    /// <summary>Off means loopback only, which is what almost everyone wants.</summary>
    public bool ControlAllowOtherComputers { get; set; }

    [JsonPropertyName("controlToken")]
    public string? ProtectedControlToken { get; set; }

    [JsonIgnore]
    public string? ControlToken
    {
        get => SecretProtector.Unprotect(ProtectedControlToken);
        set => ProtectedControlToken = SecretProtector.Protect(value);
    }

    // ---- MIDI control (I11)

    /// <summary>The MIDI input SIRS listens to, by name. Empty means none.</summary>
    public string? MidiDeviceName { get; set; }

    /// <summary>Which controller numbers do what, saved as "action=cc" pairs.</summary>
    public string? MidiBindings { get; set; }

    /// <summary>Drives the first-run wizard (I2). Set once the user has completed or skipped it.</summary>
    public bool SetupCompleted { get; set; }

    /// <summary>Connect to the selected server as soon as SIRS starts (H6).</summary>
    public bool AutoConnectOnStart { get; set; }

    /// <summary>Go on air when sound appears and off again after a long silence (G6).</summary>
    public bool AutoAirEnabled { get; set; }

    /// <summary>How long silence must last before SIRS takes itself off air. Minutes, not seconds.</summary>
    public int AutoAirStopAfterMinutes { get; set; } = 5;

    /// <summary>Keep SIRS in the notification area instead of the taskbar when minimised (I4).</summary>
    public bool MinimiseToTray { get; set; } = true;

    public double SilenceAlertSeconds { get; set; } = 15;

    /// <summary>Language code, or "en" (I8). Unknown codes fall back to English.</summary>
    public string LanguageCode { get; set; } = "en";

    /// <summary>
    /// Which palette to draw with (I5). Following Windows is the default and is right most of the
    /// time - but it is a default, not a rule. A studio PC is often left on the system light theme
    /// by whoever set it up, while the person sitting at it at midnight wants a dark window.
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Whether SIRS looks for a newer release (I9). Off by default: a check tells whoever answers
    /// that this machine is running SIRS, and nobody agreed to that by installing an encoder.
    /// </summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>Which loudness target the LUFS readout is judged against (B8).</summary>
    public string LoudnessTargetName { get; set; } = LoudnessTarget.Default.Name;

    /// <summary>The three-band compressor preset, by name (E4). "Off" means no processing.</summary>
    public string ProcessingPresetName { get; set; } = ProcessingPreset.Off.Name;

    // Bass, middle and treble on the mix (E5), in dB.
    public float ToneLowDb { get; set; }

    public float ToneMidDb { get; set; }

    public float ToneHighDb { get; set; }

    public RecordingSettings ToRecordingSettings() => new()
    {
        Folder = RecordingFolder,
        FilenameTemplate = RecordingFilenameTemplate,
        Format = RecordingFormat,
        SplitMinutes = RecordingSplitMinutes,
    };

    /// <summary>Recording formats, as the picker shows them.</summary>
    public static IReadOnlyList<(RecordingFormat Format, string Label)> RecordingFormatOptions =>
        RecordingSettings.FormatOptions;
}

public sealed class SettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path = path ?? AppPaths.SettingsFile;

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Defaults are always safe here, and losing preferences is not worth blocking startup.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));

            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, null);
            }
            else
            {
                File.Move(temporary, _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing the user can act on mid-broadcast; settings will be retried on next change.
        }
    }
}
