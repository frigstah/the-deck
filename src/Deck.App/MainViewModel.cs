using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Deck.Core;
using Deck.Core.Audio;
using Deck.Core.Audio.Dsp;
using Deck.Core.Codecs;
using Deck.Core.Control;
using Deck.Core.Diagnostics;
using Deck.Core.Localisation;
using Deck.Core.Metadata;
using Deck.Core.Recording;
using Deck.Core.Servers;
using Deck.Core.Streaming;
using Deck.Core.Updates;

namespace Deck.App;

public sealed class MainViewModel : ObservableObject, IDisposable, IControlSurface
{
    private readonly BroadcastEngine _engine = new();
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _uiTimer;
    private readonly ControlServer _control;
    private readonly MidiControl _midi;
    private readonly MidiInput _midiInput = new();
    private RelayCommand? _cancelMidiLearnCommand;

    private AudioDevice? _selectedInput;
    private AudioDevice? _selectedSecondaryInput;
    private AudioDevice? _selectedMonitorDevice;
    private ServerProfile? _selectedServer;
    private string _statusMessage = string.Empty;
    private string _nowPlayingInput = string.Empty;
    private bool _editingNowPlaying;
    private bool _suppressPersist = true;
    private bool _sendToMoreThanOneServer;
    private RelayCommand? _soundCheckCommand;
    private RelayCommand? _playSoundCheckCommand;
    private RelayCommand? _updateNowPlayingCommand;
    private RelayCommand? _resetLoudnessCommand;
    private RelayCommand? _resetToneCommand;
    private RelayCommand? _copyEndpointUrlCommand;
    private RelayCommand? _copyControlUrlCommand;
    private AsyncRelayCommand? _checkUpdatesCommand;
    private AsyncRelayCommand? _installUpdateCommand;
    private bool _installingUpdate;
    private double _updateProgress;
    private RelayCommand? _openReleaseCommand;
    private readonly UpdateChecker _updates = new();
    private ReleaseInfo? _release;
    private string _updateStatus = Strings.Get(StringId.UpdateNeverChecked);
    private LoudnessTarget _loudnessTarget = LoudnessTarget.Default;
    private readonly System.Diagnostics.Stopwatch _tickClock = System.Diagnostics.Stopwatch.StartNew();

    public MainViewModel()
    {
        _settings = _settingsStore.Load();

        foreach (var profile in _profileStore.Load()) Servers.Add(profile);

        ReloadDevices();

        _selectedServer = Servers.FirstOrDefault(s => s.Id == _settings.SelectedServerId) ?? Servers.FirstOrDefault();

        // Only ids that still match a saved server survive: lists get edited and shared, and a
        // stale entry should never quietly become a destination.
        _settings.AlsoSendToServerIds = _settings.AlsoSendToServerIds
            .Where(id => id != _selectedServer?.Id && Servers.Any(s => s.Id == id))
            .ToList();

        _sendToMoreThanOneServer = _settings.AlsoSendToServerIds.Count > 0;
        RebuildExtraTargets();

        Strings.Use(_settings.LanguageCode);

        _loudnessTarget = LoudnessTarget.All.FirstOrDefault(t => t.Name == _settings.LoudnessTargetName)
            ?? LoudnessTarget.Default;

        _engine.Capture.Silence.TriggerAfterSeconds = _settings.SilenceAlertSeconds;
        _engine.Capture.InputGainDb = _settings.InputGainDb;
        _engine.Capture.VoiceEnhanceEnabled = _settings.VoiceEnhance;
        _engine.Capture.Primary.AutoGainEnabled = _settings.AutoGain;

        _engine.Capture.ProcessingPreset =
            ProcessingPreset.All.FirstOrDefault(p => p.Name == _settings.ProcessingPresetName)
            ?? ProcessingPreset.Off;

        _engine.Capture.ToneLowDb = _settings.ToneLowDb;
        _engine.Capture.ToneMidDb = _settings.ToneMidDb;
        _engine.Capture.ToneHighDb = _settings.ToneHighDb;

        _engine.Capture.Primary.Channels =
            new ChannelSelection(_settings.InputFirstChannel, _settings.InputSingleChannel);

        _engine.AutoAir.Enabled = _settings.AutoAirEnabled;
        _engine.AutoAir.StopAfterSilentSeconds = Math.Max(30, _settings.AutoAirStopAfterMinutes * 60);
        _engine.AutoAir.StartRequested += (_, _) => OnUi(AutoStart);
        _engine.AutoAir.StopRequested += (_, _) => OnUi(AutoStop);

        _engine.NowPlaying.Template = _settings.TitleTemplate;

        // The endpoint opens a listening socket, so it only comes back if it was deliberately left
        // on. Failing to bind is reported through the status line rather than blocking startup.
        if (_settings.MetadataEndpointEnabled)
        {
            _engine.NowPlaying.UseRemote(
                _settings.MetadataPort, _settings.MetadataAllowOtherComputers, _settings.MetadataToken);
        }

        // Last night's typed title comes back (F1) - but only if nothing else is now supplying
        // titles, and only into memory. Nothing is on air yet, so nothing goes out; going live
        // sends it, the same as if it had been typed a moment ago.
        if (_engine.NowPlaying.Source == MetadataSource.Manual && _settings.ManualTitle.Length > 0)
        {
            _engine.NowPlaying.SetTitle(_settings.ManualTitle);
            _nowPlayingInput = _engine.NowPlaying.Title;
        }

        // Built here rather than in a field initialiser: it takes this view model as its surface,
        // and `this` is not available until the constructor body.
        _control = new ControlServer(this);
        _control.CommandHandled += (_, _) => OnUi(RaiseControlState);

        if (_settings.ControlEndpointEnabled)
        {
            _control.Start(_settings.ControlPort, _settings.ControlAllowOtherComputers, _settings.ControlToken);
        }

        _midi = new MidiControl(this);
        _midi.Load(_settings.MidiBindings);
        BuildMidiBindings();

        // Messages arrive on a MIDI callback thread. Handle() reaches the surface, which marshals to
        // the UI thread itself, so this only has to bring the display back.
        _midiInput.MessageReceived += (_, message) =>
        {
            var result = _midi.Handle(message);
            OnUi(() =>
            {
                if (result is { Ok: true, Message: { Length: > 0 } text }) StatusMessage = text;

                SaveMidi();
                RaiseMidiState();
            });
        };

        if (!string.IsNullOrWhiteSpace(_settings.MidiDeviceName)) _midiInput.Start(_settings.MidiDeviceName);

        _engine.Recorder.LowDiskSpace += (_, e) => OnUi(() => StatusMessage = e.Message);
        _engine.Recorder.FileCompleted += (_, e) => OnUi(() =>
        {
            StatusMessage = $"Saved {Path.GetFileName(e.FinishedPath)} and started a new file.";
            Raise(nameof(RecordingStatus));
        });
        _engine.Monitor.Volume = _settings.MonitorVolume;

        InputDevicesView = CollectionViewSource.GetDefaultView(InputDevices);
        InputDevicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AudioDevice.CategoryLabel)));

        _engine.Broadcast.TargetStateChanged += OnTargetStateChanged;
        _engine.Broadcast.ServerTypeDetected += OnServerTypeDetected;
        foreach (var entry in _engine.Log.Entries) LogEntries.Add(entry);
        _engine.Log.EntryAdded += (_, entry) => OnUi(() =>
        {
            LogEntries.Add(entry);

            // The window only ever shows the recent past; the file on disk keeps the rest.
            while (LogEntries.Count > 300) LogEntries.RemoveAt(0);
        });

        _engine.ListenerCountChanged += (_, _) =>
            OnUi(() => RaiseAll(nameof(ListenerText), nameof(ListenerTooltip), nameof(MiniListenerSuffix)));

        _engine.Capture.CaptureFailed += OnCaptureFailed;
        _engine.DeviceRecovered += (_, e) => OnUi(() =>
        {
            StatusMessage = e.Message;
            RaiseAll(nameof(SecondarySourceEnabled), nameof(WaitingForDevice));
        });
        _engine.Capture.Silence.SilenceStarted += (_, _) => OnUi(() => Raise(nameof(SilenceAlert)));
        _engine.Capture.Silence.SilenceEnded += (_, _) => OnUi(() => Raise(nameof(SilenceAlert)));
        _engine.SoundCheck.StateChanged += (_, _) => OnUi(RaiseSoundCheckState);
        _engine.Recorder.Failed += (_, e) => OnUi(() => StatusMessage = e.Message);
        _engine.NowPlaying.TitleChanged += (_, e) => OnUi(() =>
        {
            _nowPlayingInput = e.Title;
            RaiseAll(nameof(NowPlayingInput), nameof(NowPlayingStatus));
            RaiseNowPlayingFooter();
        });

        _engine.Capture.Secondary.GainDb = _settings.SecondaryGainDb;

        StartAudio();

        if (_settings.SecondaryEnabled && _selectedSecondaryInput is not null) SecondarySourceEnabled = true;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => Tick();
        _uiTimer.Start();

        _suppressPersist = false;
    }

    // ---------------------------------------------------------------- the deck, and getting off it

    private bool _isSetupOpen;
    private bool _isMiniMode;
    private RelayCommand? _openSetupCommand;
    private RelayCommand? _closeSetupCommand;

    /// <summary>
    /// Whether the setup panel is over the top of the deck.
    /// <para>
    /// The deck is what you look at while the show is going out, and it holds no settings at all —
    /// so everything that configures anything lives behind this one flag. That is the whole design:
    /// you cannot reach a setting by accident at 3 a.m., because reaching one is a deliberate act
    /// that covers the screen and tells you it has.
    /// </para>
    /// </summary>
    public bool IsSetupOpen
    {
        get => _isSetupOpen;
        private set
        {
            if (!Set(ref _isSetupOpen, value)) return;

            // The deck carries its own state when it is visible; the strip carries it when setup is
            // covering the deck. Exactly one of them is on screen at a time, and neither moment
            // leaves you unable to see that you are live.
            RaiseAll(nameof(ShowStatusStrip), nameof(ShowDeck));
        }
    }

    /// <summary>The strip only earns its height while the deck is hidden behind setup.</summary>
    public bool ShowStatusStrip => IsSetupOpen;

    /// <summary>
    /// Whether Deck is a thin strip instead of the whole deck (I4's neighbour).
    /// <para>
    /// Three sizes, and this is the middle one. The deck is what you watch when Deck is what you are
    /// doing; the notification area is for when it is out of mind entirely; the strip is for the case
    /// in between, which is most of a show - the music is coming from something else, that something
    /// else wants the screen, and all you need from Deck is the meter, where it is going, and the
    /// button that takes you off air.
    /// </para>
    /// <para>
    /// It holds no settings and no way to reach them, exactly like the deck. Everything on it is
    /// either a fact about the show or one of the three controls you touch during one.
    /// </para>
    /// </summary>
    public bool IsMiniMode
    {
        get => _isMiniMode;
        set
        {
            if (!Set(ref _isMiniMode, value)) return;

            // Setup and the strip cannot both be true: setup is a screenful and the strip is 56
            // pixels tall. Nothing offers the strip from inside setup, but the flag says so anyway
            // rather than relying on that staying true.
            if (value) IsSetupOpen = false;

            _settings.MiniMode = value;
            Persist();

            RaiseAll(nameof(ShowDeck), nameof(ShowFullTitleBar));
        }
    }

    /// <summary>The deck proper: not behind setup, and not shrunk to the strip.</summary>
    public bool ShowDeck => !IsSetupOpen && !IsMiniMode;

    /// <summary>
    /// The strip draws its own caption buttons, because a 40-pixel title bar above a 56-pixel strip
    /// would be nearly half of it spent saying what the strip already says.
    /// </summary>
    public bool ShowFullTitleBar => !IsMiniMode;

    public RelayCommand OpenSetupCommand => _openSetupCommand ??= new RelayCommand(() => IsSetupOpen = true);

    public RelayCommand CloseSetupCommand => _closeSetupCommand ??= new RelayCommand(() => IsSetupOpen = false);

    /// <summary>
    /// The signal path along the bottom of the deck. It began as facts you would otherwise open setup
    /// to check; the input, the destination and the quality are now also the fastest way to change
    /// them, because a singer moving between venues does that far more often than anything in setup.
    /// </summary>
    public string InputChip => _selectedInput?.Name ?? "no input";

    /// <summary>
    /// The input as the chip says it: short enough that a device name does not decide the width of
    /// the whole row. See <see cref="AudioDevice.ShortName"/> for what gets dropped and why.
    /// </summary>
    public string InputChipShort => _selectedInput?.ShortName ?? "none";

    /// <summary>
    /// What the input chip no longer says on its face. The chip reads "Input" so the row keeps one
    /// fixed width, which means the hover is now the quickest way to answer "which input is this?" -
    /// so it leads with the answer and mentions what the chip does second.
    /// </summary>
    public string InputChipTooltip => CanChangeSignalPath
        ? $"Input: {InputChip}. Click to change it without opening setup."
        : $"Input: {InputChip}. {LockReason}";

    public string ServerChipTooltip => CanChangeSignalPath
        ? SelectedServerSummary
        : $"{SelectedServerSummary} {LockReason}";

    public string QualityChipTooltip => CanChangeSignalPath
        ? QualitySummary
        : $"{QualitySummary} {LockReason}";

    /// <summary>
    /// Whether the deck's three chips - input, destination, quality - can be changed right now.
    /// <para>
    /// False while a show is on air or a recording is running, because every one of these interrupts
    /// what is running: changing the input restarts capture, a different server is a new connection,
    /// and a different bitrate is a new encoder. There is no version of any of them that does not
    /// leave a gap.
    /// </para>
    /// <para>
    /// This replaced a padlock the user set by hand. The padlock was a manual answer to a question
    /// Deck can answer itself - the input is only dangerous to change while something is running, and
    /// Deck knows exactly when that is - so it was one more control to notice, understand and
    /// remember to set, guarding against a case the app could simply refuse. Setup can still change
    /// the destination mid-show, behind a confirmation: that is a deliberate trip through a settings
    /// pane, not a chip you might brush past on the way to the Go live button.
    /// </para>
    /// </summary>
    public bool CanChangeSignalPath => !StreamState.IsBroadcasting() && !IsRecording;

    /// <summary>
    /// Why the chips are shut, named specifically. "Locked" on its own invites the reader to think
    /// they locked something; saying which of the two things is running tells them what to stop.
    /// </summary>
    private string LockReason => (StreamState.IsBroadcasting(), IsRecording) switch
    {
        (true, true) => "Locked while you are on air and recording.",
        (true, false) => "Locked while you are on air.",
        (false, true) => "Locked while a recording is running.",
        _ => string.Empty,
    };

    /// <summary>
    /// Changes the destination or the quality while a show is running, by taking it off air and
    /// putting it back on the new one.
    /// <para>
    /// There is no way to do this without a gap. A server change is a new connection and a quality
    /// change is a new encoder, so either way the stream stops and starts - which is why the engine
    /// only has Start and Stop rather than a swap. Rather than hide that, the deck asks: off air it
    /// changes silently, on air it says what it is about to cost.
    /// </para>
    /// <para>
    /// Returns false when the user declines, so the caller can put the picker back.
    /// </para>
    /// </summary>
    private bool ConfirmDisruptionWhileLive(string what)
    {
        if (!StreamState.IsBroadcasting()) return true;

        var answer = MessageBox.Show(
            $"You are on air. Changing {what} has to stop the stream and start it again, so " +
            "listeners will hear a few seconds of silence.\n\nChange it anyway?",
            "The Deck",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);

        return answer == System.Windows.MessageBoxResult.OK;
    }

    /// <summary>Takes the show off air and puts it straight back, on whatever is now selected.</summary>
    private void RestartShowAfterChange()
    {
        if (!StreamState.IsBroadcasting()) return;

        _engine.Log.Info("Restarting the show on the new settings.");

        // Sequenced rather than fired together: GoLive opens the new connection, and doing that
        // before the old one has let go of its socket is how you end up connected twice.
        _ = _engine.StopBroadcastAsync().ContinueWith(_ => OnUi(() =>
        {
            var profiles = ActiveProfiles();
            if (profiles.Count == 0) return;

            try
            {
                _engine.GoLive(profiles);
                RefreshTargetStatus();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// The deck's record control.
    /// <para>
    /// A button, not the chip this started as. The mockup drew "REC ●" among the signal-path chips,
    /// which were all facts rather than controls - so it was built as an indicator, and there was
    /// then no way to start a recording without opening setup. It reads as a control now and it is
    /// one; it still shows elapsed time while running, so it does the indicator's job as well.
    /// </para>
    /// </summary>
    /// <remarks>
    /// No dot in the text. The dot is drawn by the button's template as its own element, so that it
    /// can pulse while a recording runs without dragging the running time in and out of legibility.
    /// </remarks>
    public string RecordButtonLabel => IsRecording
        ? $"{_engine.Recorder.Elapsed:mm\\:ss}"
        : "REC";

    /// <summary>
    /// Says why it is running, when Deck started it rather than the user. Without this, someone who
    /// set "record every show" months ago has no way to tell whether the recording that is running
    /// is the one they meant.
    /// </summary>
    public string RecordButtonTooltip => IsRecording
        ? _recordingStartedWithShow
            ? "Recording, started automatically with the show. Press to stop."
            : "Recording. Press to stop."
        : RecordEveryShow
            ? "Press to start recording now. One will also start by itself when you go on air."
            : "Press to start recording.";

    /// <summary>
    /// The line listeners are seeing, or nothing.
    /// <para>
    /// This one readout is also a control, which nothing else on the deck is. Naming the show is
    /// the one setting that changes every time you use Deck rather than once when you set it up, so
    /// making it the only reason to open setup mid-show was wrong. It is still the same title the
    /// Track pane types, and the pane still holds everything about where titles come from.
    /// </para>
    /// </summary>
    public string NowPlayingLine => _engine.NowPlaying.Title is { Length: > 0 } title
        ? title
        : "nothing set";

    /// <summary>
    /// Whether the deck may type over the title. Only when the title is the user's own to begin
    /// with: under any other source the footer is a readout of something Deck does not own, and
    /// committing a title there would not merely be overwritten by the next poll - it would switch
    /// the source to manual and quietly cut off the station's automation.
    /// </summary>
    public bool CanTypeNowPlaying => _engine.NowPlaying.Source == MetadataSource.Manual;

    /// <summary>The footer's box is up, in place of the title.</summary>
    public bool EditingNowPlaying
    {
        get => _editingNowPlaying;
        private set
        {
            if (Set(ref _editingNowPlaying, value)) RaiseNowPlayingFooter();
        }
    }

    /// <summary>
    /// An invitation, shown only when there is nothing to show and the deck is allowed to ask.
    /// Better than the words "nothing set", which report a hole without saying who can fill it.
    /// </summary>
    public bool ShowNowPlayingSetChip => !EditingNowPlaying && CanTypeNowPlaying && NoTitleYet;

    public bool ShowNowPlayingLine => !EditingNowPlaying && !ShowNowPlayingSetChip;

    /// <summary>Null while the line is only a readout, so no tooltip promises a click that does nothing.</summary>
    public string? NowPlayingLineTooltip => CanTypeNowPlaying && !NoTitleYet
        ? "Click to change the title listeners see. Enter sets it; Esc leaves it alone."
        : null;

    private bool NoTitleYet => _engine.NowPlaying.Title.Length == 0;

    /// <summary>Opens the footer's box on whatever the title is now, ready to be typed over.</summary>
    public void BeginEditNowPlaying()
    {
        if (!CanTypeNowPlaying) return;

        NowPlayingInput = _engine.NowPlaying.Title;
        EditingNowPlaying = true;
    }

    /// <summary>Sends what was typed and closes the box.</summary>
    public void CommitEditNowPlaying()
    {
        if (!EditingNowPlaying) return;

        EditingNowPlaying = false;
        UpdateNowPlaying();
    }

    /// <summary>
    /// Closes the box and puts back the title that was there. What the deck's box sends goes out to
    /// listeners the moment it is sent, so only Enter sends: clicking away, or pressing Esc, has to
    /// be a way out rather than a way of half-naming a show in front of an audience.
    /// </summary>
    public void CancelEditNowPlaying()
    {
        if (!EditingNowPlaying) return;

        NowPlayingInput = _engine.NowPlaying.Title;
        EditingNowPlaying = false;
    }

    private void RaiseNowPlayingFooter() => RaiseAll(
        nameof(NowPlayingLine), nameof(NowPlayingLineTooltip), nameof(CanTypeNowPlaying),
        nameof(ShowNowPlayingSetChip), nameof(ShowNowPlayingLine));

    /// <summary>
    /// How full the send buffer is. Worth a corner of the deck because it is the earliest warning
    /// that a connection is struggling — it climbs well before a stream actually drops.
    /// </summary>
    public string BufferText => _engine.Broadcast.State.IsBroadcasting()
        ? $"buffer {_engine.Broadcast.BufferFill:P0}"
        : string.Empty;

    private void RaiseDeck()
    {
        RaiseAll(
            nameof(InputChip), nameof(InputChipShort), nameof(InputChipTooltip),
            nameof(RecordButtonLabel), nameof(RecordButtonTooltip), nameof(BufferText));

        RaiseNowPlayingFooter();
    }

    // ---------------------------------------------------------------- which pane is open

    /// <summary>
    /// The pane the rail is showing. An index rather than an enum because it binds straight to the
    /// TabControl that does the switching — the control already owns "one pane at a time", so there
    /// is nothing here for the view model to reimplement.
    /// <para>
    /// Clamped on the way in: a settings file written by a later version of Deck, or edited by
    /// hand, must not open on a pane that does not exist.
    /// </para>
    /// </summary>
    public int SelectedSection
    {
        get => Math.Clamp(_settings.SelectedSection, 0, SectionCount - 1);
        set
        {
            var index = Math.Clamp(value, 0, SectionCount - 1);
            if (_settings.SelectedSection == index) return;

            _settings.SelectedSection = index;
            Persist();
            Raise();
        }
    }

    /// <summary>Sound, Process, Servers, Track, Record, Control, Deck.</summary>
    private const int SectionCount = 7;

    // ---------------------------------------------------------------- pane heading readouts

    /// <summary>
    /// The short figure that sits beside a pane's title. Each one answers the question you would
    /// have to change panes to ask — what target am I aiming at, which server is this going to —
    /// so the heading row carries information rather than just repeating the rail label.
    /// </summary>
    public string LoudnessTargetShort => $"target {_loudnessTarget.Lufs:0} LUFS";

    public string SelectedServerShort => _selectedServer is null
        ? "no server yet"
        : _engine.Broadcast.IsMultiTarget
            ? $"{_selectedServer.Name} + {_engine.Broadcast.Targets.Count - 1} more"
            : _selectedServer.Name;

    public string RecordingShort => IsRecording
        ? $"recording · {_engine.Recorder.Elapsed:mm\\:ss}"
        : "not recording";

    // ---------------------------------------------------------------- devices and input level

    public ObservableCollection<AudioDevice> InputDevices { get; } = [];

    public ObservableCollection<AudioDevice> MonitorDevices { get; } = [];

    /// <summary>
    /// Inputs grouped into microphones and loopback sources (A4). Grouping matters here: without a
    /// heading, "Speakers" sitting in a list of microphones reads like a mistake.
    /// </summary>
    public ICollectionView InputDevicesView { get; }

    public AudioDevice? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (!Set(ref _selectedInput, value)) return;

            _settings.InputDeviceId = value?.Id;
            _settings.InputDeviceKind = value?.Kind ?? AudioDeviceKind.Input;
            Persist();
            StartAudio();

            RaiseAll(nameof(IsLoopbackInput), nameof(InputSourceHint), nameof(MonitorWarning),
                nameof(AdviceHint), nameof(PrimaryFaderLabel));

            // The channel choices depend on how many inputs this device turned out to have, which
            // is only known once it is open.
            RaiseInputChannels();
        }
    }

    public bool IsLoopbackInput => _selectedInput?.Kind == AudioDeviceKind.Loopback;

    /// <summary>Explains what a loopback source actually captures, since it surprises people.</summary>
    public string? InputSourceHint => IsLoopbackInput
        ? "Deck is broadcasting everything this PC plays through this device — music apps, browser tabs, everything. Sound stopping here means silence on air."
        : null;

    public double InputGainDb
    {
        get => _engine.Capture.InputGainDb;
        set
        {
            var gain = (float)Math.Round(value, 1);
            if (Math.Abs(_engine.Capture.InputGainDb - gain) < 0.05f) return;

            _engine.Capture.InputGainDb = gain;
            _settings.InputGainDb = gain;
            Persist();
            RaiseAll(nameof(InputGainDb), nameof(InputGainText));
        }
    }

    public string InputGainText => $"{(InputGainDb >= 0 ? "+" : string.Empty)}{InputGainDb:0.0} dB";

    public bool VoiceEnhance
    {
        get => _engine.Capture.VoiceEnhanceEnabled;
        set
        {
            if (_engine.Capture.VoiceEnhanceEnabled == value) return;
            _engine.Capture.VoiceEnhanceEnabled = value;
            _settings.VoiceEnhance = value;
            Persist();
            Raise();
        }
    }

    /// <summary>Automatic gain control on the main input (E3).</summary>
    public bool AutoGain
    {
        get => _engine.Capture.Primary.AutoGainEnabled;
        set
        {
            if (_engine.Capture.Primary.AutoGainEnabled == value) return;

            _engine.Capture.Primary.AutoGainEnabled = value;
            _settings.AutoGain = value;
            Persist();
            Raise();
        }
    }

    public double PeakDbLeft => _engine.Capture.InputMeter.PeakDbLeft;

    public double PeakDbRight => _engine.Capture.InputMeter.PeakDbRight;

    public string AdviceHeadline => _engine.Capture.InputMeter.Advice.Headline();

    public string AdviceHint
    {
        get
        {
            var advice = _engine.Capture.InputMeter.Advice;

            // "Check it is plugged in and not muted" is nonsense advice for a loopback source; the
            // real cause is simply that nothing is playing.
            if (advice == LevelAdvice.NoSignal && IsLoopbackInput)
            {
                return "Nothing is playing through this device. Start your music or media player, and check Windows is sending its sound here.";
            }

            return advice.Hint();
        }
    }

    /// <summary>
    /// Whether the level verdict and its sentence are on screen (B2). The meter, the numbers and the
    /// dead-air alert are deliberately not covered by this: turning off the coaching should not turn
    /// off the measurement.
    /// </summary>
    public bool ShowLevelCoaching
    {
        get => _settings.ShowLevelCoaching;
        set
        {
            if (_settings.ShowLevelCoaching == value) return;

            _settings.ShowLevelCoaching = value;
            Persist();
            RaiseAll(nameof(ShowLevelCoaching), nameof(LevelCoachingHint));
        }
    }

    public string LevelCoachingHint => ShowLevelCoaching
        ? "Deck tells you whether your level is right. Turn this off once you would rather just read the meter."
        : "Off. The meter and the numbers are still there — only the verdict is hidden.";

    /// <summary>
    /// Whether the strip's on-air sign carries the listener count as well as the state (I12).
    /// </summary>
    public bool ShowListenersOnStrip
    {
        get => _settings.ShowListenersOnStrip;
        set
        {
            if (_settings.ShowListenersOnStrip == value) return;

            _settings.ShowListenersOnStrip = value;
            Persist();
            RaiseAll(nameof(ShowListenersOnStrip), nameof(ListenersOnStripHint), nameof(MiniListenerSuffix));
        }
    }

    public string ListenersOnStripHint => ShowListenersOnStrip
        ? "The strip reads \"ON AIR WITH 7 LISTENERS\". Turn this off if the strip is somewhere other people can see it."
        : "Off. The strip reads \"ON AIR\". The count is still on the deck while you are live.";

    public Brush AdviceBrush => SeverityBrush(_engine.Capture.InputMeter.Advice.Severity());

    /// <summary>Fill behind the level verdict pill; the headline above supplies the text colour.</summary>
    public Brush AdvicePillBrush => SeveritySoftBrush(_engine.Capture.InputMeter.Advice.Severity());

    /// <summary>The verdict set as a pill reads as a badge, so it is shouted rather than sentenced.</summary>
    public string AdviceHeadlineUpper => AdviceHeadline.ToUpperInvariant();

    /// <summary>The windowed peak the coaching verdict is based on; drawn as the meter's hold marker.</summary>
    public double WindowPeakDb => _engine.Capture.InputMeter.WindowPeakDb;

    // ---------------------------------------------------------------- input channels (A7)

    /// <summary>
    /// Which of the device's inputs feed the stream. Rebuilt when the device changes, because the
    /// choices depend entirely on how many inputs it turned out to have.
    /// </summary>
    public IReadOnlyList<string> InputChannelOptions =>
        _engine.Capture.Primary.ChannelOptions.Select(o => o.Label).ToList();

    public string SelectedInputChannelLabel
    {
        get => _engine.Capture.Primary.Channels.LabelFor(DeviceChannelCount);
        set
        {
            var match = _engine.Capture.Primary.ChannelOptions.FirstOrDefault(o => o.Label == value);
            if (match.Label is null) return;

            _engine.Capture.Primary.Channels = match.Selection;
            _settings.InputFirstChannel = match.Selection.FirstChannel;
            _settings.InputSingleChannel = match.Selection.SingleChannel;
            Persist();

            RaiseAll(nameof(SelectedInputChannelLabel), nameof(InputChannelHint));
        }
    }

    private int DeviceChannelCount => Math.Max(1, _engine.Capture.Primary.DeviceFormat.Channels);

    /// <summary>Only worth showing when there is actually a choice to make.</summary>
    public bool ShowInputChannels => DeviceChannelCount > 1;

    public string InputChannelHint => DeviceChannelCount > 2
        ? $"This device has {DeviceChannelCount} inputs. Pick the one your microphone is plugged into."
        : "If your microphone only comes out of one side, choose that side here.";

    private void RaiseInputChannels() => RaiseAll(
        nameof(InputChannelOptions), nameof(SelectedInputChannelLabel), nameof(ShowInputChannels),
        nameof(InputChannelHint));

    // ---------------------------------------------------------------- processing (E4, E5)

    public IReadOnlyList<string> ProcessingPresetNames { get; } = ProcessingPreset.All.Select(p => p.Name).ToList();

    public string SelectedProcessingPresetName
    {
        get => _engine.Capture.ProcessingPreset.Name;
        set
        {
            var match = ProcessingPreset.All.FirstOrDefault(p => p.Name == value);
            if (match is null || match == _engine.Capture.ProcessingPreset) return;

            _engine.Capture.ProcessingPreset = match;
            _settings.ProcessingPresetName = match.Name;
            Persist();
            RaiseAll(nameof(SelectedProcessingPresetName), nameof(ProcessingDescription));
        }
    }

    public string ProcessingDescription => _engine.Capture.ProcessingPreset.Description;

    public double ToneLowDb
    {
        get => _engine.Capture.ToneLowDb;
        set => SetTone(value, v => _engine.Capture.ToneLowDb = v, v => _settings.ToneLowDb = v,
            nameof(ToneLowDb), nameof(ToneLowText));
    }

    public double ToneMidDb
    {
        get => _engine.Capture.ToneMidDb;
        set => SetTone(value, v => _engine.Capture.ToneMidDb = v, v => _settings.ToneMidDb = v,
            nameof(ToneMidDb), nameof(ToneMidText));
    }

    public double ToneHighDb
    {
        get => _engine.Capture.ToneHighDb;
        set => SetTone(value, v => _engine.Capture.ToneHighDb = v, v => _settings.ToneHighDb = v,
            nameof(ToneHighDb), nameof(ToneHighText));
    }

    public string ToneLowText => ToneText(ToneLowDb);

    public string ToneMidText => ToneText(ToneMidDb);

    public string ToneHighText => ToneText(ToneHighDb);

    /// <summary>Puts the tone controls back to flat, which is easier than aiming three sliders at zero.</summary>
    public RelayCommand ResetToneCommand => _resetToneCommand ??= new RelayCommand(() =>
    {
        ToneLowDb = 0;
        ToneMidDb = 0;
        ToneHighDb = 0;
    });

    private void SetTone(double value, Action<float> apply, Action<float> store, params string[] properties)
    {
        var gain = (float)Math.Round(value, 1);
        apply(gain);
        store(gain);
        Persist();
        RaiseAll(properties);
    }

    private static string ToneText(double db) =>
        Math.Abs(db) < 0.05 ? "flat" : $"{(db > 0 ? "+" : string.Empty)}{db:0.#} dB";

    // ---------------------------------------------------------------- loudness (B8)

    /// <summary>
    /// Loudness is a separate question from level. The peak meter answers "am I clipping"; this
    /// answers "how loud will this feel next to everything else the listener plays today", which is
    /// what every platform now normalises against.
    /// </summary>
    public IReadOnlyList<string> LoudnessTargetNames { get; } = LoudnessTarget.All.Select(t => t.Name).ToList();

    public string SelectedLoudnessTargetName
    {
        get => _loudnessTarget.Name;
        set
        {
            var match = LoudnessTarget.All.FirstOrDefault(t => t.Name == value);
            if (match is null || match == _loudnessTarget) return;

            _loudnessTarget = match;
            _settings.LoudnessTargetName = match.Name;
            Persist();
            RaiseLoudness();
        }
    }

    public string LoudnessTargetDetail => $"{_loudnessTarget.Detail} Target {_loudnessTarget.Lufs:0} LUFS.";

    /// <summary>The live figure, which moves with the performance.</summary>
    public string ShortTermLoudnessText => Format(_engine.Capture.Loudness?.ShortTermLufs);

    /// <summary>The gated figure for the whole session - the one a platform would act on.</summary>
    public string IntegratedLoudnessText => Format(_engine.Capture.Loudness?.IntegratedLufs);

    public string LoudnessVerdict
    {
        get
        {
            var meter = _engine.Capture.Loudness;
            if (meter is null || !meter.HasIntegrated) return "Measuring — give it half a minute of real audio.";

            return _loudnessTarget.Verdict(meter.IntegratedLufs);
        }
    }

    public Brush LoudnessBrush
    {
        get
        {
            var meter = _engine.Capture.Loudness;
            return meter is null || !meter.HasIntegrated
                ? SeverityBrush(AdviceSeverity.Neutral)
                : SeverityBrush(_loudnessTarget.Severity(meter.IntegratedLufs));
        }
    }

    /// <summary>Starts the integrated measurement again, for the top of a show.</summary>
    public RelayCommand ResetLoudnessCommand => _resetLoudnessCommand ??= new RelayCommand(() =>
    {
        _engine.Capture.Loudness?.Reset();
        RaiseLoudness();
        StatusMessage = "Loudness measurement restarted.";
    });

    private static string Format(double? lufs) =>
        lufs is null || double.IsNegativeInfinity(lufs.Value) ? "—" : $"{lufs.Value:0.0} LUFS";

    // ---------------------------------------------------------------- spectrum and phase (B9)

    /// <summary>
    /// Off by default and collapsed behind a disclosure. A spectrum is the most encoder-shaped thing
    /// in Deck, and the whole argument for the program is that the first screen does not look like
    /// one. Anyone who wants it knows what it is; anyone who does not never has to see it.
    /// </summary>
    public bool ShowAdvancedMeters
    {
        get => _settings.ShowAdvancedMeters;
        set
        {
            if (_settings.ShowAdvancedMeters == value) return;

            _settings.ShowAdvancedMeters = value;
            Persist();
            Raise();
        }
    }

    /// <summary>
    /// A fresh array each frame rather than one reused in place. A dependency property only
    /// re-renders when its value changes, and an array mutated behind the binding's back is the same
    /// reference every time - so the reused version drew the first frame and then froze. Twenty-four
    /// doubles twenty times a second is nothing beside what a WPF render allocates anyway.
    /// </summary>
    public double[] SpectrumBars
    {
        get => _spectrumBars;
        private set => Set(ref _spectrumBars, value);
    }

    private double[] _spectrumBars = new double[SpectrumAnalyser.BandCount];

    public double[] SpectrumEdgesHz => _engine.Capture.Spectrum?.BandEdgesHz ?? EmptyEdges;

    private static readonly double[] EmptyEdges = new double[SpectrumAnalyser.BandCount + 1];

    public double CorrelationValue => _engine.Capture.Correlation?.Correlation ?? 1.0;

    public string CorrelationText
    {
        get
        {
            var meter = _engine.Capture.Correlation;
            if (meter is null || meter.IsQuiet) return "—";

            // The number and what it costs, together. "+0.42" means nothing on its own; "keeps 92%
            // of the level in mono" is the part that tells someone whether to worry.
            return $"{meter.Correlation:+0.00;-0.00} — keeps {meter.MonoLevelRatio:P0} of the level in mono";
        }
    }

    public string CorrelationVerdict => _engine.Capture.Correlation?.Verdict() ?? "Nothing to measure yet.";

    /// <summary>
    /// The phase reading in three characters, to sit beside the level verdict. Compact enough to
    /// live on the Sound pane, where the full explanation would be noise — an out-of-phase input is
    /// rare, but when it happens nothing else on that screen would show it.
    /// </summary>
    public string? CorrelationSummary => _engine.Capture.Correlation is { IsQuiet: false } meter
        ? $"phase {meter.Correlation:+0.00;-0.00}"
        : null;

    public Brush CorrelationBrush =>
        SeverityBrush(_engine.Capture.Correlation?.Severity() ?? AdviceSeverity.Neutral);

    private void RaiseLoudness() => RaiseAll(
        nameof(ShortTermLoudnessText), nameof(IntegratedLoudnessText), nameof(LoudnessVerdict),
        nameof(LoudnessBrush), nameof(SelectedLoudnessTargetName), nameof(LoudnessTargetDetail),
        nameof(LoudnessTargetShort));

    // ---------------------------------------------------------------- second source (A5)

    public bool SecondarySourceEnabled
    {
        get => _engine.IsMixing;
        set
        {
            try
            {
                if (value)
                {
                    var device = _selectedSecondaryInput ?? InputDevices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Loopback);
                    if (device is null)
                    {
                        StatusMessage = "Deck could not find a second source to add.";
                        return;
                    }

                    _selectedSecondaryInput = device;
                    _engine.Capture.Secondary.GainDb = _settings.SecondaryGainDb;
                    _engine.StartSecondaryInput(device.Id, device.Kind);
                }
                else
                {
                    _engine.StopSecondaryInput();
                }

                _settings.SecondaryEnabled = value;
                Persist();
                StatusMessage = string.Empty;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }

            RaiseAll(nameof(SecondarySourceEnabled), nameof(SelectedSecondaryInput), nameof(MixHint));
        }
    }

    public AudioDevice? SelectedSecondaryInput
    {
        get => _selectedSecondaryInput;
        set
        {
            if (!Set(ref _selectedSecondaryInput, value)) return;

            _settings.SecondaryDeviceId = value?.Id;
            _settings.SecondaryDeviceKind = value?.Kind ?? AudioDeviceKind.Loopback;
            Persist();

            if (_engine.IsMixing && value is not null) _engine.StartSecondaryInput(value.Id, value.Kind);
            Raise(nameof(MixHint));
        }
    }

    public double PrimaryGainDb
    {
        get => _engine.Capture.Primary.GainDb;
        set
        {
            var gain = (float)Math.Round(value, 1);
            if (Math.Abs(_engine.Capture.Primary.GainDb - gain) < 0.05f) return;

            _engine.Capture.Primary.GainDb = gain;
            _settings.InputGainDb = gain;
            Persist();
            RaiseAll(nameof(PrimaryGainDb), nameof(InputGainDb), nameof(InputGainText));
        }
    }

    public double SecondaryGainDb
    {
        get => _engine.Capture.Secondary.GainDb;
        set
        {
            var gain = (float)Math.Round(value, 1);
            if (Math.Abs(_engine.Capture.Secondary.GainDb - gain) < 0.05f) return;

            _engine.Capture.Secondary.GainDb = gain;
            _settings.SecondaryGainDb = gain;
            Persist();
            RaiseAll(nameof(SecondaryGainDb), nameof(SecondaryGainText));
        }
    }

    public string SecondaryGainText =>
        $"{(SecondaryGainDb >= 0 ? "+" : string.Empty)}{SecondaryGainDb:0.0} dB";

    public bool PrimaryMuted
    {
        get => _engine.Capture.Primary.Muted;
        set { _engine.Capture.Primary.Muted = value; Raise(); }
    }

    public bool SecondaryMuted
    {
        get => _engine.Capture.Secondary.Muted;
        set { _engine.Capture.Secondary.Muted = value; Raise(); }
    }

    public double PrimaryPeakDb => _engine.Capture.Primary.Meter.PeakDbLeft;

    public double SecondaryPeakDb => _engine.Capture.Secondary.Meter.PeakDbLeft;

    /// <summary>Names the two faders in terms of what the user actually picked.</summary>
    public string PrimaryFaderLabel => _selectedInput?.Kind == AudioDeviceKind.Loopback
        ? "Computer sound"
        : "Microphone";

    public string? MixHint => _engine.IsMixing
        ? "Both are going out together. Pull the second fader down while you talk so your voice stays on top."
        : null;

    public string LevelReadout =>
        _engine.Capture.InputMeter.WindowPeakDb <= AudioMath.MinDb
            ? "—"
            : $"loudest {_engine.Capture.InputMeter.WindowPeakDb:0.0} dB";

    public string? SilenceAlert => _engine.Capture.Silence.IsSilent && IsLive
        ? $"No sound for {_engine.Capture.Silence.SilentSeconds:0} seconds — listeners are hearing silence."
        : null;

    // ---------------------------------------------------------------- monitoring

    public bool MonitorEnabled
    {
        get => _engine.Monitor.IsRunning;
        set
        {
            try
            {
                if (value) _engine.StartMonitoring(_selectedMonitorDevice?.Id);
                else _engine.StopMonitoring();

                _settings.MonitorEnabled = value;
                Persist();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }

            RaiseAll(nameof(MonitorEnabled), nameof(MonitorWarning));
        }
    }

    public AudioDevice? SelectedMonitorDevice
    {
        get => _selectedMonitorDevice;
        set
        {
            if (!Set(ref _selectedMonitorDevice, value)) return;

            _settings.MonitorDeviceId = value?.Id;
            Persist();

            if (_engine.Monitor.IsRunning) MonitorEnabled = true; // restart on the new device
            Raise(nameof(MonitorWarning));
        }
    }

    public double MonitorVolume
    {
        get => _engine.Monitor.Volume;
        set
        {
            _engine.Monitor.Volume = (float)Math.Clamp(value, 0, 1);
            _settings.MonitorVolume = _engine.Monitor.Volume;
            Persist();
            Raise();
        }
    }

    public string? MonitorWarning =>
        MonitorEnabled ? _engine.MonitorFeedbackWarning(_selectedMonitorDevice?.Id) : null;

    // ---------------------------------------------------------------- sound check

    public string SoundCheckButtonText => _engine.SoundCheck.State switch
    {
        SoundCheckState.Recording => "Stop recording",
        _ => "Record 10 seconds",
    };

    public bool CanPlaySoundCheck => _engine.SoundCheck.State is SoundCheckState.Ready or SoundCheckState.Playing;

    public string SoundCheckPlayText => _engine.SoundCheck.State == SoundCheckState.Playing ? "Stop" : "Play it back";

    public double SoundCheckProgress => _engine.SoundCheck.Progress;

    public bool IsSoundCheckRecording => _engine.SoundCheck.State == SoundCheckState.Recording;

    public string SoundCheckStatus => _engine.SoundCheck.State switch
    {
        SoundCheckState.Recording => $"Recording… {_engine.SoundCheck.RecordedSeconds:0.0}s — say something at your normal volume.",
        SoundCheckState.Ready or SoundCheckState.Playing =>
            _engine.SoundCheck.Summary is { } summary ? $"{summary.Headline}. {summary.Detail}" : string.Empty,
        _ => "Record a few seconds and hear yourself exactly as listeners will.",
    };

    public Brush SoundCheckBrush => _engine.SoundCheck.Summary is { } summary
        ? SeverityBrush(summary.Advice.Severity())
        : SeverityBrush(AdviceSeverity.Neutral);

    public RelayCommand SoundCheckCommand => _soundCheckCommand ??= new RelayCommand(() =>
    {
        try
        {
            if (_engine.SoundCheck.State == SoundCheckState.Recording) _engine.SoundCheck.StopRecording();
            else _engine.StartSoundCheck();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        RaiseSoundCheckState();
    });

    public RelayCommand PlaySoundCheckCommand => _playSoundCheckCommand ??= new RelayCommand(() =>
    {
        try
        {
            if (_engine.SoundCheck.State == SoundCheckState.Playing) _engine.SoundCheck.StopPlayback();
            else _engine.SoundCheck.Play(_selectedMonitorDevice?.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        RaiseSoundCheckState();
    });

    /// <summary>Stops playback without toggling it on, for when a view moves away from the check.</summary>
    public void StopSoundCheckPlayback()
    {
        _engine.SoundCheck.StopPlayback();
        RaiseSoundCheckState();
    }

    // ---------------------------------------------------------------- servers

    public ObservableCollection<ServerProfile> Servers { get; } = [];

    public ServerProfile? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (ReferenceEquals(_selectedServer, value)) return;

            // Asked before the change is made, not after, so declining leaves the picker where it
            // was rather than snapping back from somewhere it briefly went.
            if (!ConfirmDisruptionWhileLive("which server the show goes to"))
            {
                Raise(nameof(SelectedServer));
                return;
            }

            var wasLive = StreamState.IsBroadcasting();

            if (!Set(ref _selectedServer, value)) return;

            _settings.SelectedServerId = value?.Id;
            Persist();
            RebuildExtraTargets();
            RaiseAll(nameof(SelectedServerSummary), nameof(CanGoLive), nameof(QualitySummary),
                nameof(ListenUrl), nameof(SelectedServerShort),
                nameof(BroadcastTargetText), nameof(QualityShort),
                nameof(SelectedQualityOption),
                nameof(CanChangeQuality));

            if (wasLive) RestartShowAfterChange();
        }
    }

    public string SelectedServerSummary => _selectedServer?.Summary ?? "No server set up yet.";

    public string QualitySummary => _selectedServer is null
        ? string.Empty
        : $"{_selectedServer.Encoder.Summary} — {QualityPreset.BandwidthPerListener(_selectedServer.Encoder)}";

    // ---------------------------------------------------------------- quality, from the deck

    /// <summary>Lossless has no bitrate to choose, and with no server there is nothing to change.</summary>
    public bool CanChangeQuality => _selectedServer is not null && !_selectedServer.Encoder.Codec.IsLossless();

    /// <summary>The other case: a server exists but its codec has no bitrate, so state it and stop.</summary>
    public bool ShowLosslessChip => _selectedServer is not null && _selectedServer.Encoder.Codec.IsLossless();

    /// <summary>
    /// The three bitrates anyone actually picks between. 128 is what every host accepts, 192 is the
    /// usual compromise, 320 is as good as MP3 gets - and the full 32-320 range is still in the server
    /// editor for the rare case that needs something else.
    /// <para>
    /// Bitrate only. Sample rate was here briefly, paired with each bitrate, and it does not belong:
    /// it is decided once when a server is set up, from what the host asks for, and then never touched
    /// again. The deck is the surface for the things you change between songs, and putting a
    /// set-and-forget setting on it doubles the length of this list to no purpose.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> QualityOptions { get; } = ["128k", "192k", "320k"];

    /// <summary>
    /// The current bitrate, or empty when the server is set to something the deck does not offer -
    /// 256k from the server editor, say. Empty rather than a nearest guess: nothing is selected in
    /// the list, which is honest, and picking a value still works.
    /// </summary>
    public string SelectedQualityOption
    {
        get
        {
            if (_selectedServer is null) return string.Empty;

            var candidate = $"{_selectedServer.Encoder.BitrateKbps}k";
            return QualityOptions.Contains(candidate) ? candidate : string.Empty;
        }

        set
        {
            if (_selectedServer is null || string.IsNullOrEmpty(value)) return;
            if (!int.TryParse(value.TrimEnd('k'), out var kbps)) return;
            if (_selectedServer.Encoder.BitrateKbps == kbps) return;

            ApplyEncoderChange(_selectedServer.Encoder with { BitrateKbps = kbps });
        }
    }

    /// <summary>
    /// Writes new encoder settings onto the selected server and restarts whatever is using them.
    /// <para>
    /// Normalised on the way in, because not every codec accepts every rate - Opus has its own list -
    /// and a rate the encoder will refuse should be corrected here rather than thrown at the user
    /// when they next go live.
    /// </para>
    /// <para>
    /// Worth being clear that this edits the <em>server</em>, not just this show. The quality belongs
    /// to the destination, so choosing 320k from the deck is the same act as choosing it in the
    /// server editor, and it persists.
    /// </para>
    /// </summary>
    private void ApplyEncoderChange(EncoderSettings settings)
    {
        if (_selectedServer is null) return;

        var wasLive = StreamState.IsBroadcasting();

        _selectedServer.Encoder = settings.Normalised();
        SaveServers();

        // Capture runs at the encoder's rate, so a rate change means restarting the input too.
        if (!wasLive) StartAudio();

        RaiseAll(nameof(QualitySummary), nameof(QualityShort), nameof(SelectedQualityOption),
            nameof(SelectedServerSummary));

        if (wasLive) RestartShowAfterChange();
    }

    public string? ListenUrl => _selectedServer is null || string.IsNullOrWhiteSpace(_selectedServer.Host)
        ? null
        : _selectedServer.ListenUrl;

    public bool CanGoLive => _selectedServer is not null && !StreamState.IsBroadcasting();

    public void AddOrUpdateServer(ServerProfile profile)
    {
        var existing = Servers.FirstOrDefault(s => s.Id == profile.Id);
        if (existing is null)
        {
            Servers.Add(profile);
            SelectedServer = profile;
        }
        else
        {
            var index = Servers.IndexOf(existing);
            Servers[index] = profile;
            if (_selectedServer?.Id == profile.Id) SelectedServer = profile;
        }

        SaveServers();
        RebuildExtraTargets();
        RaiseAll(nameof(SelectedServerSummary), nameof(QualitySummary), nameof(ListenUrl),
            nameof(CanGoLive), nameof(SelectedServerShort),
            nameof(BroadcastTargetText), nameof(QualityShort));
    }

    public void RemoveServer(ServerProfile profile)
    {
        Servers.Remove(profile);
        _settings.AlsoSendToServerIds.Remove(profile.Id);

        if (_selectedServer?.Id == profile.Id) SelectedServer = Servers.FirstOrDefault();

        SaveServers();
        RebuildExtraTargets();
        Persist();
    }

    public void SaveServers() => _profileStore.Save(Servers);

    /// <summary>Serialises the server list for sharing between DJs (C10).</summary>
    public string ExportServers() => ProfileStore.Export(Servers);

    /// <summary>
    /// Adds servers from a shared file. Ids are regenerated on collision so importing a file that
    /// came from a copy of your own list adds servers rather than silently replacing them.
    /// </summary>
    public int ImportServers(string json)
    {
        var added = ProfileStore.MergeInto(Servers, ProfileStore.Import(json));

        if (added > 0)
        {
            SaveServers();
            SelectedServer ??= Servers.FirstOrDefault();
            RebuildExtraTargets();
            RaiseAll(nameof(SelectedServerSummary), nameof(QualitySummary), nameof(CanGoLive),
                nameof(SelectedServerShort), nameof(BroadcastTargetText), nameof(QualityShort));
        }

        return added;
    }

    /// <summary>How many imported servers still need a password typed in.</summary>
    public int ServersMissingPassword => Servers.Count(s => string.IsNullOrEmpty(s.Password));

    // ---------------------------------------------------------------- extra destinations (C12)

    /// <summary>The other saved servers, each with a tick box for joining the broadcast.</summary>
    public ObservableCollection<ExtraTargetRow> ExtraTargets { get; } = [];

    /// <summary>
    /// Whether the "also send to" list is showing. Not persisted on its own: turning it off clears
    /// the choices, so the saved id list is always exactly what will go out. A hidden list that
    /// still streams somewhere would be the worst kind of surprise.
    /// </summary>
    public bool SendToMoreThanOneServer
    {
        get => _sendToMoreThanOneServer;
        set
        {
            if (!Set(ref _sendToMoreThanOneServer, value)) return;

            if (!value)
            {
                foreach (var row in ExtraTargets) row.IsSelected = false;
            }

            RaiseAll(nameof(HasOtherServers), nameof(ExtraTargetsHint));
        }
    }

    public bool HasOtherServers => ExtraTargets.Count > 0;

    public string ExtraTargetsHint => ExtraTargets.Count == 0
        ? "Add a second server above and it will appear here."
        : "The same show goes to every ticked server at once. One of them dropping does not take the others off air.";

    private void RebuildExtraTargets()
    {
        var chosen = _settings.AlsoSendToServerIds.ToHashSet();

        ExtraTargets.Clear();
        foreach (var profile in Servers.Where(s => s.Id != _selectedServer?.Id))
        {
            ExtraTargets.Add(new ExtraTargetRow(profile, chosen.Contains(profile.Id), OnExtraTargetChanged));
        }

        RaiseAll(nameof(HasOtherServers), nameof(ExtraTargetsHint), nameof(TargetCountSummary));
    }

    private void OnExtraTargetChanged()
    {
        _settings.AlsoSendToServerIds = ExtraTargets.Where(t => t.IsSelected).Select(t => t.Profile.Id).ToList();
        Persist();

        // Adding a server that wants a higher sample rate changes the capture format, so the
        // pipeline is rebuilt now rather than at the moment of going live.
        if (!StreamState.IsBroadcasting()) StartAudio();

        RaiseAll(nameof(TargetCountSummary), nameof(QualitySummary), nameof(BroadcastTargetText));
    }

    /// <summary>The destinations this broadcast will use: the chosen server first, then the extras.</summary>
    private List<ServerProfile> ActiveProfiles()
    {
        var profiles = new List<ServerProfile>();
        if (_selectedServer is not null) profiles.Add(_selectedServer);

        if (_sendToMoreThanOneServer)
        {
            profiles.AddRange(ExtraTargets.Where(t => t.IsSelected).Select(t => t.Profile));
        }

        return profiles;
    }

    public string TargetCountSummary
    {
        get
        {
            var count = ActiveProfiles().Count;
            return count <= 1 ? string.Empty : $"Going to {count} servers.";
        }
    }

    /// <summary>Per-destination status, shown only while more than one server is in play.</summary>
    public ObservableCollection<TargetStatusRow> TargetStatus { get; } = [];

    public bool ShowTargetStatus => _engine.Broadcast.IsMultiTarget;

    public string? BroadcastDetail => _engine.Broadcast.StatusDetail;

    private void RefreshTargetStatus()
    {
        TargetStatus.Clear();
        foreach (var target in _engine.Broadcast.Targets) TargetStatus.Add(new TargetStatusRow(target));

        RaiseAll(nameof(ShowTargetStatus), nameof(BroadcastDetail), nameof(SelectedServerShort));
    }

    // ---------------------------------------------------------------- broadcast

    public StreamState StreamState => _engine.Broadcast.State;

    public bool IsLive => StreamState == Core.Streaming.StreamState.Live;

    public string StateHeadline => StreamState.Headline();

    public Brush StateBrush => StreamState switch
    {
        Core.Streaming.StreamState.Live => (Brush)AppResource("LiveBrush"),
        Core.Streaming.StreamState.Failed => (Brush)AppResource("BadBrush"),
        Core.Streaming.StreamState.Reconnecting or Core.Streaming.StreamState.Connecting => (Brush)AppResource("WarnBrush"),
        _ => (Brush)AppResource("MutedTextBrush"),
    };

    public string GoLiveButtonText => StreamState.IsBroadcasting() ? "Stop broadcasting" : "Go live";

    /// <summary>
    /// The on-air control in the status strip is filled while off air and outlined while live.
    /// <para>
    /// Deliberately not <see cref="StateBrush"/>, which is grey off air and made the one button the
    /// whole program exists for look disabled. And deliberately asymmetric: going on air should be
    /// an inviting target, while taking a station off air should not be something the hand finds by
    /// accident. The state block to its left already says, loudly, that the show is out.
    /// </para>
    /// </summary>
    public Brush GoLiveButtonBrush => StreamState.IsBroadcasting()
        ? (Brush)AppResource("SurfaceBrush")
        : (Brush)AppResource("AccentBrush");

    public Brush GoLiveButtonTextBrush => StreamState.IsBroadcasting()
        ? (Brush)AppResource("TextBrush")
        : (Brush)AppResource("OnAccentBrush");

    public Brush GoLiveButtonBorderBrush => StreamState.IsBroadcasting()
        ? (Brush)AppResource("BorderBrush")
        : (Brush)AppResource("AccentBrush");

    /// <summary>The state block is signage, so it shouts.</summary>
    public string StateHeadlineUpper => StateHeadline.ToUpperInvariant();

    public string UptimeText
    {
        get
        {
            var uptime = _engine.Broadcast.Uptime;
            return uptime == TimeSpan.Zero ? "—" : uptime.ToString(uptime.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        }
    }

    public string SentText
    {
        get
        {
            var sent = _engine.Broadcast.BytesSent;
            var megabytes = sent / 1024.0 / 1024.0;
            return megabytes < 1 ? $"{sent / 1024.0:0} KB sent" : $"{megabytes:0.0} MB sent";
        }
    }

    /// <summary>
    /// Where the show is going, as the strip says it: the server's name, plus how many others are
    /// getting the same audio. The mockup's "Main + 1 backup" - what you are broadcasting to
    /// is exactly the sort of thing that should not require changing pane to check.
    /// </summary>
    public string BroadcastTargetText
    {
        get
        {
            if (_selectedServer is null) return string.Empty;

            var extras = ActiveProfiles().Count - 1;
            return extras switch
            {
                <= 0 => _selectedServer.Name,
                1 => $"{_selectedServer.Name} + 1 backup",
                _ => $"{_selectedServer.Name} + {extras} backups",
            };
        }
    }

    /// <summary>Format and bitrate, compactly, e.g. "MP3 256k".</summary>
    public string QualityShort => _selectedServer?.Encoder.ShortSummary ?? string.Empty;

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    // ---------------------------------------------------------------- listeners and log

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public SessionLog Log => _engine.Log;

    /// <summary>
    /// Listener count for the deck and the strip.
    /// <para>
    /// "No listener count" rather than an empty space, and that is the whole of this wave's point. An
    /// empty space could mean two opposite things - nobody has tuned in, or this server never tells
    /// Deck - and a station owner staring at one cannot tell which. Now nought listeners says nought,
    /// and not knowing says it does not know, with the reason on hover.
    /// </para>
    /// </summary>
    public string ListenerText
    {
        get
        {
            if (!StreamState.IsBroadcasting()) return string.Empty;

            return _engine.ListenerCount switch
            {
                null => Strings.Get(StringId.ListenerUnknown),
                1 => Strings.Get(StringId.ListenerOne),
                var count => Strings.Get(StringId.ListenerMany, count),
            };
        }
    }

    /// <summary>Where the number came from, or why there is not one. Never the only place it is said.</summary>
    public string ListenerTooltip => _engine.ListenerDetail ?? string.Empty;

    /// <summary>
    /// The listener count as a tail on the mini strip's state block, so it reads "ON AIR WITH 18
    /// LISTENERS" - one sign answering both questions.
    /// <para>
    /// The strip is the only place this belongs. The deck already says the count in the readout row
    /// beside the meter, and putting it in the headline there as well would state one fact twice on one
    /// screen; the strip has no readouts at all, which is exactly why it needs it in the sign.
    /// </para>
    /// <para>
    /// Live only, and empty unless the number is actually known. "ON AIR WITH NO LISTENER COUNT" would
    /// be a worse sign than "ON AIR", and a count carried through a reconnect is a number from before
    /// the drop - so <see cref="IsLive"/> rather than IsBroadcasting.
    /// </para>
    /// <para>
    /// It was briefly shown off air too, on the argument that people waiting on a server's fallback
    /// mount are worth knowing about before you start. Reverted: it made Deck poll somebody's server
    /// every fifteen seconds while doing nothing, and "OFF AIR WITH 12 LISTENERS" invites the reading
    /// that twelve people are listening to a broadcast that is not happening.
    /// </para>
    /// </summary>
    public string MiniListenerSuffix =>
        ShowListenersOnStrip && IsLive && _engine.ListenerCount is not null
            ? Strings.Get(StringId.StateWithListeners, ListenerText).ToUpperInvariant()
            : string.Empty;

    /// <summary>
    /// The widest tail the strip will ever draw, for a hidden twin to claim the width with.
    /// <para>
    /// Without it the state block resizes every time the count changes, and since the meter takes
    /// whatever width is left, the meter would shift sideways a few pixels each time somebody tuned in
    /// or out - the same fault the record button's hidden twin exists to prevent, and the same one that
    /// once had the meter visibly breathing with the audio it was measuring. Built from the same
    /// template as the real thing so it stays right in any language.
    /// </para>
    /// <para>
    /// Three digits. A station that gets its thousandth listener widens the block once, which is a
    /// problem worth having.
    /// </para>
    /// </summary>
    public string MiniListenerReserve =>
        Strings.Get(StringId.StateWithListeners, Strings.Get(StringId.ListenerMany, 999)).ToUpperInvariant();

    public void ToggleBroadcast()
    {
        if (StreamState.IsBroadcasting())
        {
            // Before the stream closes, so the recording covers the whole show rather than stopping
            // a moment after the last audio went out.
            StopRecordingWithShow();

            _ = _engine.StopBroadcastAsync().ContinueWith(_ => OnUi(RefreshTargetStatus),
                TaskScheduler.Default);
            return;
        }

        var profiles = ActiveProfiles();

        if (profiles.Count == 0)
        {
            StatusMessage = "Add a server first, then press Go live.";
            return;
        }

        // Every destination is checked before any of them is opened. Going half on air and then
        // stopping to report a typo in the backup would be worse than not starting.
        foreach (var profile in profiles)
        {
            var problems = profile.Validate();
            if (problems.Count == 0) continue;

            StatusMessage = profiles.Count == 1 ? problems[0] : $"{profile.Name}: {problems[0]}";
            return;
        }

        try
        {
            StatusMessage = string.Empty;
            _engine.GoLive(profiles);
            RefreshTargetStatus();

            if (RecordEveryShow && !IsRecording) StartRecordingWithShow();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>
    /// Recording that began because the show did, rather than because someone pressed record.
    /// <para>
    /// Tracked so that coming off air stops it again - but only when Deck was the one that started
    /// it. Someone who pressed record before going on air is recording deliberately, and having the
    /// end of a broadcast silently end their recording too would be the wrong call.
    /// </para>
    /// </summary>
    private bool _recordingStartedWithShow;

    private void StartRecordingWithShow()
    {
        ToggleRecording();
        _recordingStartedWithShow = IsRecording;

        if (IsRecording) _engine.Log.Info("Recording started with the show.");
    }

    private void StopRecordingWithShow()
    {
        if (!_recordingStartedWithShow || !IsRecording) return;

        _recordingStartedWithShow = false;
        _engine.Log.Info("Show ended — stopping the recording it started.");
        ToggleRecording();
    }

    /// <summary>
    /// Going on air because sound appeared (G6). It reuses the same path as pressing Go live, so
    /// the validation and the log entry are identical - the only difference is who asked.
    /// </summary>
    private void AutoStart()
    {
        if (StreamState.IsBroadcasting() || ActiveProfiles().Count == 0) return;

        _engine.Log.Info("Sound detected — going on air automatically.");
        ToggleBroadcast();
    }

    private void AutoStop()
    {
        if (!StreamState.IsBroadcasting()) return;

        _engine.Log.Info("Long silence — coming off air automatically.");
        ToggleBroadcast();
    }

    // ---------------------------------------------------------------- now playing

    public string NowPlayingInput
    {
        get => _nowPlayingInput;
        set => Set(ref _nowPlayingInput, value);
    }

    public string NowPlayingStatus
    {
        get
        {
            if (_engine.NowPlaying.SourceProblem is { } problem) return problem;

            var title = _engine.NowPlaying.Title;

            if (_engine.NowPlaying.SuspendUpdates)
            {
                return string.IsNullOrWhiteSpace(title)
                    ? "On hold — listeners keep seeing the last title sent."
                    : $"On hold — listeners keep seeing the last title sent. Waiting to send: {title}";
            }

            return _engine.NowPlaying.Source switch
            {
                MetadataSource.TextFile =>
                    $"Reading from {Path.GetFileName(_engine.NowPlaying.FilePath)}. " +
                    (string.IsNullOrWhiteSpace(title) ? "Nothing in it yet." : $"Listeners see: {title}"),

                MetadataSource.MediaSession when string.IsNullOrWhiteSpace(title) =>
                    "Following Windows. Start playing something and the title will appear here.",

                MetadataSource.MediaSession =>
                    _engine.NowPlaying.MediaSessionApp is { } app
                        ? $"From {app} — listeners see: {title}"
                        : $"Listeners see: {title}",

                MetadataSource.Remote when string.IsNullOrWhiteSpace(title) =>
                    "Waiting for your playout software to send the first title.",

                _ => string.IsNullOrWhiteSpace(title) ? "Nothing sent yet." : $"Listeners see: {title}",
            };
        }
    }

    // ---------------------------------------------------------------- title format and hold (F5)

    public string TitleTemplateText
    {
        get => _engine.NowPlaying.Template;
        set
        {
            if (_engine.NowPlaying.Template == value) return;

            _engine.NowPlaying.Template = value;
            _settings.TitleTemplate = value;
            Persist();
            RaiseAll(nameof(TitleTemplateText), nameof(TitleTemplatePreview));
        }
    }

    /// <summary>What the template does to a made-up track, so the effect is visible while typing.</summary>
    public string TitleTemplatePreview => $"Listeners would see: {TitleTemplate.Preview(TitleTemplateText)}";

    public IReadOnlyList<string> TitleTemplateExamples { get; } = TitleTemplate.Examples;

    /// <summary>
    /// Holds the current title on air. The point is adverts and jingles: a listener seeing
    /// "Sweeper 14b" in their player learns nothing and the station looks broken.
    /// </summary>
    public bool HoldNowPlaying
    {
        get => _engine.NowPlaying.SuspendUpdates;
        set
        {
            _engine.NowPlaying.SuspendUpdates = value;
            RaiseAll(nameof(HoldNowPlaying), nameof(NowPlayingStatus));
        }
    }

    // ---------------------------------------------------------------- automation endpoint (F4)

    public bool MetadataEndpointEnabled
    {
        get => _engine.NowPlaying.Server.IsRunning;
        set
        {
            if (value)
            {
                _engine.NowPlaying.UseRemote(
                    _settings.MetadataPort, _settings.MetadataAllowOtherComputers, _settings.MetadataToken);
            }
            else
            {
                _engine.NowPlaying.Server.Stop();
                _engine.NowPlaying.UseManual();
            }

            _settings.MetadataEndpointEnabled = _engine.NowPlaying.Server.IsRunning;
            Persist();
            RaiseEndpointState();
        }
    }

    public int MetadataPort
    {
        get => _settings.MetadataPort;
        set
        {
            if (_settings.MetadataPort == value) return;

            _settings.MetadataPort = value;
            Persist();
            RestartEndpointIfRunning();
        }
    }

    public bool MetadataAllowOtherComputers
    {
        get => _settings.MetadataAllowOtherComputers;
        set
        {
            if (_settings.MetadataAllowOtherComputers == value) return;

            _settings.MetadataAllowOtherComputers = value;
            Persist();
            RestartEndpointIfRunning();
        }
    }

    public string MetadataToken
    {
        get => _settings.MetadataToken ?? string.Empty;
        set
        {
            _settings.MetadataToken = value;
            Persist();
            RestartEndpointIfRunning();
        }
    }

    public string MetadataEndpointStatus
    {
        get
        {
            var server = _engine.NowPlaying.Server;

            if (server.Problem is { } problem) return problem;
            if (!server.IsRunning) return "Off. Turn it on to let your playout software send titles to Deck.";

            var reach = server.AllowOtherComputers
                ? "Reachable from other computers on your network."
                : "Reachable from this computer only.";

            var seen = server.UpdatesReceived == 0
                ? "Nothing has come in yet."
                : $"{server.UpdatesReceived} update(s) received.";

            return $"Listening on port {server.Port}. {reach} {seen}";
        }
    }

    public string MetadataEndpointUrl => _engine.NowPlaying.Server.ExampleUrl;

    public bool ShowMetadataEndpointDetails => _engine.NowPlaying.Server.IsRunning;

    public RelayCommand CopyEndpointUrlCommand => _copyEndpointUrlCommand ??= new RelayCommand(() =>
    {
        try
        {
            Clipboard.SetText(MetadataEndpointUrl);
            StatusMessage = "Address copied. Paste it into your playout software.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deck could not copy that: {ex.Message}";
        }
    });

    private void RestartEndpointIfRunning()
    {
        if (_engine.NowPlaying.Server.IsRunning || _settings.MetadataEndpointEnabled)
        {
            _engine.NowPlaying.UseRemote(
                _settings.MetadataPort, _settings.MetadataAllowOtherComputers, _settings.MetadataToken);
        }

        RaiseEndpointState();
    }

    private void RaiseEndpointState() => RaiseAll(
        nameof(MetadataEndpointEnabled), nameof(MetadataEndpointStatus), nameof(MetadataEndpointUrl),
        nameof(MetadataPort), nameof(MetadataAllowOtherComputers), nameof(MetadataToken),
        nameof(ShowMetadataEndpointDetails), nameof(NowPlayingStatus), nameof(TitleFromAnotherSource));

    /// <summary>
    /// True when something other than the person at the deck is supplying the title - a watched
    /// file, Windows, or the endpoint - so the box is read-only until they take it back.
    /// </summary>
    public bool TitleFromAnotherSource => _engine.NowPlaying.Source != MetadataSource.Manual;

    public bool UsingMediaSession => _engine.NowPlaying.Source == MetadataSource.MediaSession;

    public async Task UseMediaSessionAsync()
    {
        await _engine.NowPlaying.UseMediaSessionAsync();
        RaiseMetadataState();
    }

    public RelayCommand UpdateNowPlayingCommand =>
        _updateNowPlayingCommand ??= new RelayCommand(UpdateNowPlaying);

    /// <summary>
    /// Sends whatever is in the box: the deck footer's line and the Track pane's Send button. Both
    /// are somebody typing a title, so both take over from any other source. Callers that only want
    /// a title on air go through <see cref="IControlSurface.SetTitle"/> instead.
    /// </summary>
    private void UpdateNowPlaying()
    {
        _engine.NowPlaying.SetManualTitle(NowPlayingInput);

        // Remembered from here rather than from the service's TitleChanged, which every source
        // fires: only a title somebody typed is theirs to keep (F1).
        _settings.ManualTitle = _engine.NowPlaying.Title;
        Persist();

        RaiseMetadataState();
    }

    public void UseMetadataFile(string path)
    {
        _engine.NowPlaying.UseTextFile(path);
        RaiseMetadataState();
    }

    public void UseManualMetadata()
    {
        _engine.NowPlaying.UseManual();
        RaiseMetadataState();
    }

    private void RaiseMetadataState()
    {
        RaiseAll(
            nameof(NowPlayingStatus), nameof(TitleFromAnotherSource), nameof(UsingMediaSession),
            nameof(NowPlayingInput));

        // Changing where titles come from changes whether the deck's own line can be typed over.
        RaiseNowPlayingFooter();
    }

    // ---------------------------------------------------------------- recording

    public bool IsRecording => _engine.Recorder.IsRecording;

    public string RecordButtonText => IsRecording ? "Stop recording" : "Start recording";

    public string RecordingStatus
    {
        get
        {
            if (!IsRecording) return $"Recordings are saved to {_settings.RecordingFolder}";

            var elapsed = _engine.Recorder.Elapsed;
            var size = _engine.Recorder.BytesWritten / 1024.0 / 1024.0;
            return $"Recording {Path.GetFileName(_engine.Recorder.CurrentFilePath)} — {elapsed:mm\\:ss}, {size:0.0} MB";
        }
    }

    public void ToggleRecording()
    {
        try
        {
            if (IsRecording)
            {
                var path = _engine.StopRecording();
                StatusMessage = path is null ? string.Empty : $"Saved {Path.GetFileName(path)}";
            }
            else
            {
                var encoder = _selectedServer?.Encoder ?? QualityPreset.Default.Settings;
                var station = _selectedServer?.StationName is { Length: > 0 } name ? name : "The Deck";
                _engine.StartRecording(_settings.ToRecordingSettings(), encoder, station);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        RaiseAll(nameof(IsRecording), nameof(RecordButtonText), nameof(RecordingStatus),
            nameof(RecordButtonLabel), nameof(RecordButtonTooltip),
            nameof(RecordingShort));

        RaiseChipLock();
    }

    /// <summary>
    /// Start recording whenever the show starts, and stop when it ends.
    /// <para>
    /// The setting has existed since the first version and nothing read it, so it did nothing at all.
    /// It is wired now.
    /// </para>
    /// </summary>
    public bool RecordEveryShow
    {
        get => _settings.RecordWhileBroadcasting;
        set
        {
            if (_settings.RecordWhileBroadcasting == value) return;

            _settings.RecordWhileBroadcasting = value;
            Persist();

            // Turned on while already live: start now rather than waiting for the next show, which
            // is what someone reaching for this switch mid-broadcast is asking for.
            if (value && StreamState.IsBroadcasting() && !IsRecording) StartRecordingWithShow();

            RaiseAll(nameof(RecordEveryShow), nameof(RecordEveryShowHint), nameof(RecordingStatus));
        }
    }

    public string RecordEveryShowHint => RecordEveryShow
        ? "Every show is kept. Recording starts when you go on air and stops when you come off."
        : "Recording is only ever started by hand.";

    public IReadOnlyList<(int Minutes, string Label)> SplitOptions { get; } = RecordingSettings.SplitOptions;

    public string SelectedSplitLabel
    {
        get => SplitOptions.FirstOrDefault(o => o.Minutes == _settings.RecordingSplitMinutes).Label
               ?? SplitOptions[0].Label;
        set
        {
            var match = SplitOptions.FirstOrDefault(o => o.Label == value);
            if (match.Label is null) return;

            _settings.RecordingSplitMinutes = match.Minutes;
            Persist();
            Raise();
        }
    }

    public IReadOnlyList<string> SplitLabels { get; } = RecordingSettings.SplitOptions.Select(o => o.Label).ToList();

    public IReadOnlyList<string> RecordingFormatLabels { get; } =
        RecordingSettings.FormatOptions.Select(o => o.Label).ToList();

    public string SelectedRecordingFormatLabel
    {
        get => RecordingSettings.FormatOptions.FirstOrDefault(o => o.Format == _settings.RecordingFormat).Label
               ?? RecordingSettings.FormatOptions[0].Label;
        set
        {
            var match = RecordingSettings.FormatOptions.FirstOrDefault(o => o.Label == value);
            if (match.Label is null) return;

            _settings.RecordingFormat = match.Format;
            Persist();
            Raise();
        }
    }

    public string RecordingFolder
    {
        get => _settings.RecordingFolder;
        set
        {
            _settings.RecordingFolder = value;
            Persist();
            RaiseAll(nameof(RecordingFolder), nameof(RecordingStatus));
        }
    }

    // ---------------------------------------------------------------- remote control (I10)

    /// <summary>
    /// Every command arrives on a socket thread, so each one is marshalled to the UI thread before
    /// it touches anything. The alternative - letting a network caller run <see cref="ToggleBroadcast"/>
    /// concurrently with the user pressing the button - is a race that would only ever show up live.
    /// </summary>
    private static T OnUiBlocking<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        // No dispatcher at all means there is no window - the checks drive this interface directly.
        return dispatcher is null || dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }

    ControlStatus IControlSurface.Status() => OnUiBlocking(() => new ControlStatus
    {
        State = StateHeadline,
        IsLive = IsLive,
        Station = _selectedServer?.StationName,
        Destinations = _engine.Broadcast.Targets
            .Select(t => $"{t.Name} — {t.State.Headline()}")
            .ToList(),
        NowPlaying = string.IsNullOrWhiteSpace(_engine.NowPlaying.Title) ? null : _engine.NowPlaying.Title,
        Uptime = _engine.Broadcast.Uptime,
        Listeners = _engine.ListenerCount,
        PeakDb = _engine.Capture.InputMeter.WindowPeakDb,
        Lufs = _engine.Capture.Loudness is { HasIntegrated: true } meter ? meter.IntegratedLufs : null,
        IsRecording = IsRecording,
        IsMuted = PrimaryMuted,
        RecordingFile = IsRecording ? Path.GetFileName(_engine.Recorder.CurrentFilePath) : null,
        IsAudioRunning = _engine.IsAudioRunning,
        Problem = string.IsNullOrWhiteSpace(StatusMessage) ? SilenceAlert : StatusMessage,
    });

    Task<ControlResult> IControlSurface.GoLiveAsync() => Task.FromResult(OnUiBlocking(() =>
    {
        if (StreamState.IsBroadcasting()) return ControlResult.Refused($"Already {StateHeadline.ToLowerInvariant()}.");

        // Cleared first so that whatever ToggleBroadcast puts there is this command's answer and
        // not something left on screen from ten minutes ago.
        StatusMessage = string.Empty;
        ToggleBroadcast();

        return StreamState.IsBroadcasting()
            ? ControlResult.Done("Going on air.")
            : ControlResult.Refused(string.IsNullOrWhiteSpace(StatusMessage)
                ? "Deck could not go on air."
                : StatusMessage);
    }));

    Task<ControlResult> IControlSurface.GoOffAsync() => Task.FromResult(OnUiBlocking(() =>
    {
        if (!StreamState.IsBroadcasting()) return ControlResult.Refused("Not on air.");

        ToggleBroadcast();
        return ControlResult.Done("Coming off air.");
    }));

    ControlResult IControlSurface.SetTitle(string title) => OnUiBlocking(() =>
    {
        // Pushed rather than typed: this must not switch Deck to manual titles. A station running
        // the now-playing endpoint (F4) that sends one title from here would otherwise lose the
        // endpoint for the rest of the session - and settings still saying it is on makes that look
        // intermittent. Refused rather than quietly ignored where another source does own the
        // title: automation fighting the media-session watcher needs to be told, or it will look
        // like Deck is dropping updates.
        if (!_engine.NowPlaying.TryPushTitle(title))
        {
            return ControlResult.Refused(
                "Deck is taking titles from somewhere else at the moment, so this one was not used.");
        }

        // Not remembered. A title arriving over the endpoint is almost always a track from playout
        // software, one of a few hundred in an evening - keeping it would write the settings file on
        // every track change and leave last night's last song as tomorrow's opening title. What the
        // user typed themselves is a different thing, and that is what gets kept.
        NowPlayingInput = _engine.NowPlaying.Title;
        RaiseMetadataState();

        return ControlResult.Done($"Now playing: {_engine.NowPlaying.Title}");
    });

    ControlResult IControlSurface.StartRecording() => OnUiBlocking(() =>
    {
        if (IsRecording) return ControlResult.Refused("Already recording.");

        StatusMessage = string.Empty;
        ToggleRecording();

        return IsRecording
            ? ControlResult.Done($"Recording to {Path.GetFileName(_engine.Recorder.CurrentFilePath)}")
            : ControlResult.Refused(string.IsNullOrWhiteSpace(StatusMessage)
                ? "Deck could not start recording."
                : StatusMessage);
    });

    ControlResult IControlSurface.StopRecording() => OnUiBlocking(() =>
    {
        if (!IsRecording) return ControlResult.Refused("Not recording.");

        ToggleRecording();
        return ControlResult.Done("Recording stopped.");
    });

    ControlResult IControlSurface.SetMuted(bool muted) => OnUiBlocking(() =>
    {
        PrimaryMuted = muted;
        return ControlResult.Done(muted ? "Input muted." : "Input unmuted.");
    });

    ControlResult IControlSurface.SetGainDb(double db) => OnUiBlocking(() =>
    {
        var clamped = Math.Clamp(db, -30, 30);
        InputGainDb = clamped;

        return Math.Abs(clamped - db) < 0.001
            ? ControlResult.Done($"Input level set to {clamped:0.0} dB.")
            : ControlResult.Done($"Input level set to {clamped:0.0} dB, the nearest Deck allows.");
    });

    public bool ControlEndpointEnabled
    {
        get => _control.IsRunning;
        set
        {
            if (value)
            {
                _control.Start(_settings.ControlPort, _settings.ControlAllowOtherComputers, _settings.ControlToken);
            }
            else
            {
                _control.Stop();
            }

            _settings.ControlEndpointEnabled = _control.IsRunning;
            Persist();
            RaiseControlState();
        }
    }

    public int ControlPort
    {
        get => _settings.ControlPort;
        set
        {
            if (_settings.ControlPort == value) return;

            _settings.ControlPort = value;
            Persist();
            RestartControlIfRunning();
        }
    }

    public bool ControlAllowOtherComputers
    {
        get => _settings.ControlAllowOtherComputers;
        set
        {
            if (_settings.ControlAllowOtherComputers == value) return;

            _settings.ControlAllowOtherComputers = value;
            Persist();
            RestartControlIfRunning();
        }
    }

    public string ControlToken
    {
        get => _settings.ControlToken ?? string.Empty;
        set
        {
            _settings.ControlToken = value;
            Persist();
            RestartControlIfRunning();
        }
    }

    public string ControlEndpointStatus
    {
        get
        {
            if (_control.Problem is { } problem) return problem;

            if (!_control.IsRunning)
            {
                return "Off. Turn it on to let other programs, or Deck's own command line, drive Deck.";
            }

            var reach = _control.AllowOtherComputers
                ? "Anything on your network can control Deck with the password."
                : "This computer only.";

            var seen = _control.CommandsHandled == 0
                ? "No commands yet."
                : $"{_control.CommandsHandled} command(s), last was {_control.LastCommand}.";

            return $"Listening on port {_control.Port}. {reach} {seen}";
        }
    }

    public string ControlEndpointUrl => _control.ExampleUrl;

    public bool ShowControlEndpointDetails => _control.IsRunning;

    public RelayCommand CopyControlUrlCommand => _copyControlUrlCommand ??= new RelayCommand(() =>
    {
        try
        {
            Clipboard.SetText(ControlEndpointUrl);
            StatusMessage = "Address copied.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deck could not copy that: {ex.Message}";
        }
    });

    private void RestartControlIfRunning()
    {
        if (_control.IsRunning || _settings.ControlEndpointEnabled)
        {
            _control.Start(_settings.ControlPort, _settings.ControlAllowOtherComputers, _settings.ControlToken);
        }

        RaiseControlState();
    }

    private void RaiseControlState() => RaiseAll(
        nameof(ControlEndpointEnabled), nameof(ControlEndpointStatus), nameof(ControlEndpointUrl),
        nameof(ControlPort), nameof(ControlAllowOtherComputers), nameof(ControlToken),
        nameof(ShowControlEndpointDetails));

    // ---------------------------------------------------------------- MIDI control (I11)

    public IReadOnlyList<string> MidiDevices => MidiInput.Devices();

    public string? SelectedMidiDevice
    {
        get => _settings.MidiDeviceName;
        set
        {
            if (_settings.MidiDeviceName == value) return;

            _settings.MidiDeviceName = value;
            Persist();

            if (string.IsNullOrWhiteSpace(value)) _midiInput.Stop();
            else _midiInput.Start(value);

            RaiseMidiState();
        }
    }

    public string MidiStatus
    {
        get
        {
            if (_midi.IsLearning) return $"Move the control you want to use for \"{_midi.Learning.Label()}\".";
            if (_midiInput.Problem is { } problem) return problem;

            if (!_midiInput.IsRunning)
            {
                return MidiDevices.Count == 0
                    ? "No MIDI controllers found. Plug one in and reopen Deck."
                    : "Off. Choose a controller to use physical buttons and faders.";
            }

            return _midi.LastMessage is { } last
                ? $"Listening to {_midiInput.DeviceName}. Last saw {last}."
                : $"Listening to {_midiInput.DeviceName}.";
        }
    }

    public ObservableCollection<MidiBindingRow> MidiBindings { get; } = [];

    /// <summary>Cancels a learn that was started and then thought better of.</summary>
    public RelayCommand CancelMidiLearnCommand => _cancelMidiLearnCommand ??= new RelayCommand(() =>
    {
        _midi.CancelLearning();
        RaiseMidiState();
    });

    public bool IsLearningMidi => _midi.IsLearning;

    private void BuildMidiBindings()
    {
        MidiBindings.Clear();

        foreach (var action in MidiActions.Assignable)
        {
            MidiBindings.Add(new MidiBindingRow(
                action,
                _midi.For(action)?.Describe(),
                new RelayCommand(() => { _midi.Learn(action); RaiseMidiState(); }),
                new RelayCommand(() => { _midi.Clear(action); SaveMidi(); RaiseMidiState(); })));
        }
    }

    private void SaveMidi()
    {
        _settings.MidiBindings = _midi.Save();
        Persist();
    }

    private void RaiseMidiState()
    {
        BuildMidiBindings();
        RaiseAll(nameof(MidiStatus), nameof(SelectedMidiDevice), nameof(IsLearningMidi), nameof(MidiDevices));
    }

    // ---------------------------------------------------------------- setup state

    public bool NeedsSetup => !_settings.SetupCompleted || Servers.Count == 0;

    public void MarkSetupCompleted()
    {
        _settings.SetupCompleted = true;
        Persist();
        Raise(nameof(NeedsSetup));
    }

    public AppSettings Settings => _settings;

    public bool AutoConnectOnStart
    {
        get => _settings.AutoConnectOnStart;
        set { _settings.AutoConnectOnStart = value; Persist(); Raise(); }
    }

    // ---------------------------------------------------------------- automatic on-air (G6)

    public bool AutoGoLiveOnSound
    {
        get => _engine.AutoAir.Enabled;
        set
        {
            _engine.AutoAir.Enabled = value;
            _settings.AutoAirEnabled = value;
            Persist();
            RaiseAll(nameof(AutoGoLiveOnSound), nameof(AutoAirStatus));
        }
    }

    public string? AutoAirStatus => _engine.AutoAir.Status(StreamState.IsBroadcasting());

    // ---------------------------------------------------------------- connection health (H7)

    /// <summary>
    /// What the link is actually doing. The buffer figure is the useful one: a rate close to the
    /// configured bitrate with an empty buffer is healthy, and a buffer that keeps climbing is the
    /// warning that comes before audio starts being dropped.
    /// </summary>
    public string? ConnectionStats
    {
        get
        {
            if (!StreamState.IsBroadcasting()) return null;

            var broadcast = _engine.Broadcast;
            var parts = new List<string> { $"{broadcast.ThroughputKbps:0} kbps out" };

            var fill = broadcast.BufferFill;
            parts.Add(fill < 0.02 ? "buffer clear" : $"buffer {fill:P0}");

            if (broadcast.DroppedBlocks > 0) parts.Add($"{broadcast.DroppedBlocks} block(s) dropped");
            if (broadcast.ReconnectAttempts > 0) parts.Add($"{broadcast.ReconnectAttempts} reconnect(s)");

            return string.Join(" · ", parts);
        }
    }

    public Brush ConnectionStatsBrush => SeverityBrush(
        _engine.Broadcast.BufferFill switch
        {
            > 0.5 => AdviceSeverity.Bad,
            > 0.2 => AdviceSeverity.Warning,
            _ => AdviceSeverity.Neutral,
        });

    public bool MinimiseToTray
    {
        get => _settings.MinimiseToTray;
        set { _settings.MinimiseToTray = value; Persist(); Raise(); }
    }

    public string HotkeyDescription => GlobalHotkeys.Description;

    // ---------------------------------------------------------------- language (I8)

    /// <summary>English plus any translation files found on disk, with how complete each one is.</summary>
    public IReadOnlyList<string> LanguageOptions =>
        Strings.Available().Select(DescribeLanguage).ToList();

    public string SelectedLanguage
    {
        get => DescribeLanguage(Strings.Current);
        set
        {
            var match = Strings.Available().FirstOrDefault(p => DescribeLanguage(p) == value);
            if (match is null) return;

            Strings.Use(match.Code);
            _settings.LanguageCode = match.Code;
            Persist();

            // Everything that came from the catalogue is already on screen, so the whole view is
            // refreshed rather than trying to work out which labels moved.
            RaiseAll(nameof(SelectedLanguage), nameof(AdviceHeadline), nameof(AdviceHeadlineUpper),
                nameof(AdviceHint),
                nameof(StateHeadline), nameof(GoLiveButtonText), nameof(ListenerText),
                nameof(LanguageHint));
        }
    }

    public string LanguageHint =>
        $"Translations are JSON files in {Strings.Directory}. Anything a translation has not covered stays in English.";

    // ---------------------------------------------------------------- appearance (I5)

    /// <summary>
    /// The palettes offered, paired with the setting each one stores. Following Windows is first
    /// because it is the default and the right answer for most people; the other two are for the
    /// ones it is wrong for, which is why they exist at all.
    /// </summary>
    private static readonly (string Label, AppTheme Theme)[] Themes =
    [
        ("Follow Windows", AppTheme.System),
        ("Light", AppTheme.Light),
        ("Dark", AppTheme.Dark),
    ];

    public IReadOnlyList<string> ThemeOptions => Themes.Select(t => t.Label).ToList();

    public string SelectedTheme
    {
        get => Themes.First(t => t.Theme == _settings.Theme).Label;
        set
        {
            var match = Themes.FirstOrDefault(t => t.Label == value);
            if (match.Label is null || match.Theme == _settings.Theme) return;

            _settings.Theme = match.Theme;
            Persist();
            App.UseTheme(match.Theme);

            RaiseAll(nameof(SelectedTheme), nameof(ThemeHint));
        }
    }

    public string ThemeHint => _settings.Theme == AppTheme.System
        ? "Deck changes with the Windows light and dark setting, as soon as you change it."
        : "Deck stays on this palette whatever Windows is set to.";

    /// <summary>
    /// Whether setup slides. Same three states as the palette, and in the same order: the one that is
    /// right for most people first, then the two answers for whom it is wrong.
    /// </summary>
    private static readonly (string Label, SetupMotion Motion)[] Motions =
    [
        ("Follow Windows", SetupMotion.System),
        ("Always slide", SetupMotion.Always),
        ("Never slide", SetupMotion.Never),
    ];

    public IReadOnlyList<string> SetupMotionOptions => Motions.Select(m => m.Label).ToList();

    public string SelectedSetupMotion
    {
        get => Motions.First(m => m.Motion == _settings.SetupMotion).Label;
        set
        {
            var match = Motions.FirstOrDefault(m => m.Label == value);
            if (match.Label is null || match.Motion == _settings.SetupMotion) return;

            _settings.SetupMotion = match.Motion;
            Persist();
            RaiseAll(nameof(SelectedSetupMotion), nameof(SetupMotionHint));
        }
    }

    /// <summary>
    /// Says what Windows is currently set to when Deck is following it, because otherwise "Follow
    /// Windows" gives no clue why setup does or does not move - which is the whole reason this setting
    /// exists.
    /// </summary>
    public string SetupMotionHint => _settings.SetupMotion switch
    {
        SetupMotion.Always => "Setup slides in and out whatever Windows is set to.",
        SetupMotion.Never => "Setup appears and disappears with no movement.",
        _ => System.Windows.SystemParameters.ClientAreaAnimation
            ? "Windows has animation effects on, so setup slides."
            : "Windows has animation effects turned off, so setup appears without moving. "
              + "Choose \"Always slide\" if you want the movement anyway.",
    };

    /// <summary>Writes a starting file for someone who wants to translate Deck.</summary>
    public string ExportLanguageTemplate(string code, string name) => Strings.ExportTemplate(code, name);

    private static string DescribeLanguage(LanguagePack pack)
    {
        if (pack.Code == "en") return pack.Name;

        var coverage = pack.Coverage(Strings.English.Keys.ToList());
        return coverage >= 0.999 ? pack.Name : $"{pack.Name} — {coverage:P0} translated";
    }

    // ---------------------------------------------------------------- updates (I9)

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value) return;

            _settings.CheckForUpdates = value;
            Persist();
            Raise();

            if (value) _ = CheckForUpdatesAsync();
        }
    }

    public string VersionText => $"Deck {UpdateChecker.CurrentVersion}";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public bool UpdateAvailable => _release is not null && UpdateChecker.CanOpen(_release);

    public AsyncRelayCommand CheckUpdatesCommand => _checkUpdatesCommand ??= new AsyncRelayCommand(CheckForUpdatesAsync);

    /// <summary>Opens the release page in the browser, for anyone who would rather install by hand.</summary>
    public RelayCommand OpenReleasePageCommand => _openReleaseCommand ??= new RelayCommand(() =>
    {
        var url = _release is not null && UpdateChecker.CanOpen(_release)
            ? _release.Url
            : UpdateChecker.ReleasesPage;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deck could not open that page: {ex.Message}";
        }
    });

    private async Task CheckForUpdatesAsync()
    {
        UpdateStatus = "Checking…";

        var result = await _updates.CheckAsync().ConfigureAwait(true);

        _release = result.Available ? result.Release : null;
        UpdateStatus = result.Summary;
        RaiseUpdateState();
    }

    // ---------------------------------------------------------------- installing an update (I9)

    /// <summary>
    /// True once a newer release has been found and it carries something Deck can install. A
    /// release with no update package, or one Deck cannot write over, leaves this false and the
    /// user with the release page instead — which is what Deck did for its first four phases.
    /// </summary>
    public bool CanInstallUpdate =>
        _release?.UpdatePayload is not null && !IsInstallingUpdate && UpdateInstaller.CanInstallInPlace(out _);

    public bool IsInstallingUpdate
    {
        get => _installingUpdate;
        private set => Set(ref _installingUpdate, value);
    }

    public double UpdateProgressFraction
    {
        get => _updateProgress;
        private set => Set(ref _updateProgress, value);
    }

    public string InstallUpdateButtonText => _release is null
        ? "Install the update"
        : $"Install {_release.DisplayVersion} and restart";

    /// <summary>
    /// Downloads the release, checks it against its published digest, and hands over to it.
    /// <para>
    /// Refuses outright while on air. Taking a station off the air to install an update is not a
    /// thing Deck should decide to do, and an update that waits ten minutes costs nothing.
    /// </para>
    /// </summary>
    public AsyncRelayCommand InstallUpdateCommand => _installUpdateCommand ??= new AsyncRelayCommand(async () =>
    {
        if (_release is null || IsInstallingUpdate) return;

        if (StreamState.IsBroadcasting())
        {
            UpdateStatus = "Deck is on air. Come off air first — an update restarts the program.";
            return;
        }

        IsInstallingUpdate = true;
        UpdateProgressFraction = 0;
        RaiseUpdateState();

        var installer = new UpdateInstaller();
        installer.Progress += (_, p) => OnUi(() =>
        {
            UpdateProgressFraction = p.Fraction;
            UpdateStatus = $"{p.Stage}… {p.Fraction:P0}";
        });

        var result = await installer.InstallAsync(_release).ConfigureAwait(true);

        UpdateStatus = result.Message;

        if (!result.Ok)
        {
            IsInstallingUpdate = false;
            RaiseUpdateState();
            return;
        }

        // The replacement is already running and waiting for this process to let go of its files.
        // Closing the window runs the normal shutdown, so servers and settings are saved first.
        _engine.Log.Info($"Installing update {_release.DisplayVersion}. Deck will restart.");
        UpdateRequested?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Raised when an update has been staged and the window should close so it can land.</summary>
    public event EventHandler? UpdateRequested;

    private void RaiseUpdateState() => RaiseAll(
        nameof(UpdateAvailable), nameof(CanInstallUpdate), nameof(InstallUpdateButtonText),
        nameof(IsInstallingUpdate), nameof(UpdateProgressFraction));

    /// <summary>Mutes or unmutes the main input. Bound to a global hotkey (I3).</summary>
    public void ToggleMute()
    {
        PrimaryMuted = !PrimaryMuted;
        StatusMessage = PrimaryMuted
            ? "Microphone muted. Listeners cannot hear you."
            : "Microphone unmuted.";
    }

    // ---------------------------------------------------------------- plumbing

    public void ReloadDevices()
    {
        var previousInputId = _selectedInput?.Id ?? _settings.InputDeviceId;
        var previousInputKind = _selectedInput?.Kind ?? _settings.InputDeviceKind;
        var previousMonitor = _selectedMonitorDevice?.Id ?? _settings.MonitorDeviceId;

        InputDevices.Clear();
        foreach (var device in AudioDevices.AllInputSources()) InputDevices.Add(device);

        // A program only appears in that list while Windows says it is playing, so a chosen one has to
        // be put back when it is paused - otherwise refreshing while the backing track is stopped moves
        // the second source to something nobody picked, and the singer finds out on stage (A9).
        KeepChosenProgram(previousInputId, previousInputKind);
        KeepChosenProgram(
            _selectedSecondaryInput?.Id ?? _settings.SecondaryDeviceId,
            _selectedSecondaryInput?.Kind ?? _settings.SecondaryDeviceKind);

        MonitorDevices.Clear();
        foreach (var device in AudioDevices.Outputs()) MonitorDevices.Add(device);

        // Match on id and kind together, then fall back to a real input rather than silently
        // landing on a loopback source the user never chose.
        _selectedInput = InputDevices.FirstOrDefault(d => d.Matches(previousInputId, previousInputKind))
            ?? InputDevices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Input && d.IsSystemDefault)
            ?? InputDevices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Input)
            ?? InputDevices.FirstOrDefault();

        _selectedSecondaryInput =
            InputDevices.FirstOrDefault(d => d.Matches(
                _selectedSecondaryInput?.Id ?? _settings.SecondaryDeviceId,
                _selectedSecondaryInput?.Kind ?? _settings.SecondaryDeviceKind))
            ?? InputDevices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Loopback && d.IsSystemDefault)
            ?? InputDevices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Loopback);

        _selectedMonitorDevice = MonitorDevices.FirstOrDefault(d => d.Id == previousMonitor)
            ?? MonitorDevices.FirstOrDefault(d => d.IsSystemDefault)
            ?? MonitorDevices.FirstOrDefault();

        InputDevicesView?.Refresh();

        RaiseAll(nameof(SelectedInput), nameof(SelectedSecondaryInput), nameof(SelectedMonitorDevice),
            nameof(IsLoopbackInput), nameof(InputSourceHint), nameof(MonitorWarning),
            nameof(PrimaryFaderLabel));
    }

    /// <summary>Puts a chosen program back into the list when it is not currently playing.</summary>
    private void KeepChosenProgram(string? deviceId, AudioDeviceKind kind)
    {
        if (kind != AudioDeviceKind.Process || !ProcessLoopbackCapture.IsProcessId(deviceId)) return;
        if (InputDevices.Any(d => d.Matches(deviceId, AudioDeviceKind.Process))) return;

        InputDevices.Add(AudioProcesses.Named(deviceId!));
    }

    private void StartAudio()
    {
        try
        {
            var profiles = ActiveProfiles();
            var format = profiles.Count > 0
                ? BroadcastSet.CaptureFormatFor(profiles)
                : QualityPreset.Default.Settings.Format;

            _engine.StartAudio(_selectedInput?.Id, _selectedInput?.Kind ?? AudioDeviceKind.Input, format);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void Tick()
    {
        var elapsed = _tickClock.Elapsed.TotalSeconds;
        _tickClock.Restart();

        _engine.AutoAir.Update(
            (float)_engine.Capture.InputMeter.WindowPeakDb, StreamState.IsBroadcasting(), elapsed);

        if (_engine.AutoAir.Enabled) Raise(nameof(AutoAirStatus));

        RaiseAll(
            nameof(PeakDbLeft), nameof(PeakDbRight), nameof(WindowPeakDb), nameof(AdviceHeadline),
            nameof(AdviceHeadlineUpper), nameof(AdvicePillBrush),
            nameof(AdviceHint), nameof(AdviceBrush), nameof(LevelReadout), nameof(SilenceAlert),
            nameof(CorrelationSummary),
            nameof(PrimaryPeakDb), nameof(SecondaryPeakDb), nameof(WaitingForDevice),
            nameof(ShortTermLoudnessText), nameof(IntegratedLoudnessText), nameof(LoudnessVerdict),
            nameof(LoudnessBrush));

        // Only while the panel is open. The read itself takes a lock the audio thread also wants,
        // and there is no reason to pay for it twenty times a second to update something hidden.
        if (ShowAdvancedMeters)
        {
            var bars = new double[SpectrumAnalyser.BandCount];
            _engine.Capture.Spectrum?.Read(bars, elapsed);
            SpectrumBars = bars;

            RaiseAll(nameof(SpectrumEdgesHz), nameof(CorrelationValue),
                nameof(CorrelationText), nameof(CorrelationVerdict), nameof(CorrelationBrush));
        }

        if (StreamState.IsBroadcasting())
        {
            RaiseAll(nameof(UptimeText), nameof(SentText), nameof(BroadcastDetail),
                nameof(ConnectionStats), nameof(ConnectionStatsBrush));
        }

        // Only while the deck is the thing on screen. Behind setup these are covered up, and
        // refreshing six readouts twenty times a second to update nothing is waste.
        if (!IsSetupOpen) RaiseDeck();

        if (IsRecording) Raise(nameof(RecordingStatus));
        if (IsSoundCheckRecording) RaiseAll(nameof(SoundCheckProgress), nameof(SoundCheckStatus));
    }

    private void RaiseSoundCheckState() => RaiseAll(
        nameof(SoundCheckButtonText), nameof(CanPlaySoundCheck), nameof(SoundCheckPlayText),
        nameof(SoundCheckStatus), nameof(SoundCheckBrush), nameof(SoundCheckProgress),
        nameof(IsSoundCheckRecording));

    private void OnTargetStateChanged(object? sender, TargetStateChangedEventArgs e) => OnUi(() =>
    {
        // With a backup running, a message has to say which server it came from, or "Reconnecting"
        // reads as though the whole show has dropped.
        var prefix = _engine.Broadcast.IsMultiTarget ? $"{e.Target.Name}: " : string.Empty;

        if (!string.IsNullOrWhiteSpace(e.Message)) StatusMessage = prefix + e.Message;
        else if (StreamState == Core.Streaming.StreamState.Live) StatusMessage = string.Empty;

        RefreshTargetStatus();

        RaiseAll(
            nameof(StreamState), nameof(IsLive), nameof(StateHeadline), nameof(StateBrush),
            nameof(GoLiveButtonText), nameof(GoLiveButtonBrush), nameof(GoLiveButtonTextBrush),
            nameof(GoLiveButtonBorderBrush), nameof(StateHeadlineUpper), nameof(CanGoLive),
            nameof(UptimeText), nameof(SilenceAlert),
            // The listener readout only exists while on air, so going off has to clear it rather than
            // waiting for the next poll to notice. The strip's version of it says the state as well, so
            // it has to be told here rather than only when a count arrives.
            nameof(ListenerText), nameof(ListenerTooltip), nameof(MiniListenerSuffix));

        RaiseChipLock();
    });

    /// <summary>
    /// The chips shut and open with the show and the recording, so both of those transitions have to
    /// say so. Raised from here rather than from the twenty-times-a-second tick, which would be
    /// twenty notifications a second to report no change.
    /// </summary>
    private void RaiseChipLock() => RaiseAll(
        nameof(CanChangeSignalPath), nameof(InputChipTooltip), nameof(ServerChipTooltip),
        nameof(QualityChipTooltip));

    /// <summary>
    /// Connecting worked out what kind of server this is. Saved straight away rather than left in
    /// memory: the point of detecting it is that it only ever has to happen once, and the editor and
    /// the listener count both read the type off the saved profile.
    /// </summary>
    private void OnServerTypeDetected(object? sender, EventArgs e) => OnUi(() =>
    {
        SaveServers();
        RaiseAll(nameof(SelectedServerSummary), nameof(SelectedServerShort),
            nameof(ListenUrl));
    });

    /// <summary>Shown while a dropped-out device is being waited for (A6).</summary>
    public string? WaitingForDevice => _engine.IsWaitingForDevice
        ? "Waiting for the audio input to come back. Deck will pick it up on its own as soon as it does."
        : null;

    private void OnCaptureFailed(object? sender, CaptureFailedEventArgs e) => OnUi(() =>
    {
        StatusMessage = e.Message;

        // Deliberately not re-picking a device here: the watchdog is already waiting for this exact
        // one, and swapping the selection would fight it.
        Raise(nameof(WaitingForDevice));
    });

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static object AppResource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) ?? Brushes.Gray;

    private static Brush SeverityBrush(AdviceSeverity severity) => severity switch
    {
        AdviceSeverity.Ok => (Brush)AppResource("OkBrush"),
        AdviceSeverity.Warning => (Brush)AppResource("WarnBrush"),
        AdviceSeverity.Bad => (Brush)AppResource("BadBrush"),
        _ => (Brush)AppResource("MutedTextBrush"),
    };

    /// <summary>The tinted fill behind a verdict pill, paired with <see cref="SeverityBrush"/>.</summary>
    private static Brush SeveritySoftBrush(AdviceSeverity severity) => severity switch
    {
        AdviceSeverity.Ok => (Brush)AppResource("OkSoftBrush"),
        AdviceSeverity.Warning => (Brush)AppResource("WarnSoftBrush"),
        AdviceSeverity.Bad => (Brush)AppResource("BadSoftBrush"),
        _ => (Brush)AppResource("NeutralSoftBrush"),
    };

    private void Persist()
    {
        if (_suppressPersist) return;
        _settingsStore.Save(_settings);
    }

    public void Dispose()
    {
        _uiTimer.Stop();

        // Before the engine goes: the handshake file must not outlive the endpoint, or the next
        // Deck --live would aim at a port nobody is listening on.
        _control.Dispose();
        _midiInput.Dispose();

        _settingsStore.Save(_settings);
        SaveServers();
        _engine.Dispose();
    }
}
