using System.Text.Json;
using System.Text.Json.Serialization;
using Deck.Core.Audio;
using Deck.Core.Audio.Dsp;
using Deck.Core.Control;
using Deck.Core.Metadata;
using Deck.Core.Recording;
using Deck.Core.Servers;

namespace Deck.Core;

/// <summary>
/// Which palette Deck draws with (I5). Stored by name, so the settings file stays readable and a
/// value that is no longer recognised falls back to following Windows rather than to nothing.
/// </summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Everything Deck remembers between runs that is not a server profile.</summary>
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

    // There was an InputLocked setting here, for a padlock beside the deck's input chip. Both are
    // gone: the input is only dangerous to change while a show or a recording is running, Deck knows
    // when that is, and the chips now shut themselves. A setting nobody has to find beats a switch
    // everybody has to understand. Old settings files may still carry the key; it is ignored.

    /// <summary>
    /// Which pane of the window was open last. Remembered because someone who spends a show on the
    /// sound pane should not be put back on the servers pane every time Deck starts.
    /// </summary>
    public int SelectedSection { get; set; }

    // ---- remote control (I10)

    /// <summary>
    /// Whether other programs can drive Deck over HTTP. Off by default and deliberately separate
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

    /// <summary>The MIDI input Deck listens to, by name. Empty means none.</summary>
    public string? MidiDeviceName { get; set; }

    /// <summary>Which controller numbers do what, saved as "action=cc" pairs.</summary>
    public string? MidiBindings { get; set; }

    /// <summary>Drives the first-run wizard (I2). Set once the user has completed or skipped it.</summary>
    public bool SetupCompleted { get; set; }

    /// <summary>Connect to the selected server as soon as Deck starts (H6).</summary>
    public bool AutoConnectOnStart { get; set; }

    /// <summary>Go on air when sound appears and off again after a long silence (G6).</summary>
    public bool AutoAirEnabled { get; set; }

    /// <summary>How long silence must last before Deck takes itself off air. Minutes, not seconds.</summary>
    public int AutoAirStopAfterMinutes { get; set; } = 5;

    /// <summary>
    /// Hide the window from the taskbar when minimised, leaving only the icon by the clock (I4).
    /// <para>
    /// Off by default, which is a reversal. It defaulted to on when it was inherited, and two things
    /// have since made that the wrong answer. Mini mode is the real answer to "get out of my way but
    /// stay where I can see you", and it is better at it than a 16-pixel icon - so the feature that
    /// justified hiding the window has been superseded. And hiding is harder to undo than it looks:
    /// Windows 11 puts new notification icons behind the overflow chevron, so a minimised Deck can be
    /// two clicks and a hunt away rather than one double-click, with an empty taskbar in between.
    /// A window that vanishes when you minimise it is the oldest surprise in Windows software.
    /// </para>
    /// <para>
    /// Nothing is lost by leaving it off: the notification icon is there whenever Deck is running,
    /// with its on-air colour, whatever this is set to. This only decides whether the taskbar button
    /// goes away as well.
    /// </para>
    /// </summary>
    public bool MinimiseToTray { get; set; }

    /// <summary>
    /// Whether Deck is a thin strip rather than the whole deck.
    /// <para>
    /// Remembered because it is a way of working, not a passing choice: someone who parks the strip
    /// along the top of a screen while they run a playout program in the rest of it wants it there
    /// again tomorrow. It is the third size Deck has, between the deck and the notification area,
    /// and the only one where nothing but the show is on screen.
    /// </para>
    /// </summary>
    public bool MiniMode { get; set; }

    public double SilenceAlertSeconds { get; set; } = 15;

    /// <summary>
    /// Whether the level verdict - "sounds good", "too quiet" - is shown (B2). On by default,
    /// because it is the thing that teaches a first-time broadcaster what a good level looks like.
    /// Off for the people who already know: once you can read a meter, a badge telling you what you
    /// can already see is just something else moving on the screen. The meter, the numbers and the
    /// silence alert are unaffected - this hides the coaching, not the measurement.
    /// </summary>
    public bool ShowLevelCoaching { get; set; } = true;

    /// <summary>Language code, or "en" (I8). Unknown codes fall back to English.</summary>
    public string LanguageCode { get; set; } = "en";

    /// <summary>
    /// Which palette to draw with (I5).
    /// <para>
    /// Dark by default, and not because dark themes are fashionable. The Deck is an instrument panel
    /// - the point of it is that a glance tells you whether you are on air - and a lit meter on a
    /// dark ground is how every piece of broadcast equipment has answered that question for fifty
    /// years. It is also what most of this will be used in: a room at night.
    /// </para>
    /// <para>
    /// Light is a full second palette rather than an afterthought, because a studio PC is often left
    /// on the system light theme by whoever set it up. Following Windows stays available for people
    /// who want everything on their machine to agree; it is just no longer the assumption.
    /// </para>
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>
    /// Whether Deck looks for a newer release (I9). Off by default: a check tells whoever answers
    /// that this machine is running Deck, and nobody agreed to that by installing an encoder.
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
