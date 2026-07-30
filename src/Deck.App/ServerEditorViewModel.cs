using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using Deck.Core.Codecs;
using Deck.Core.Diagnostics;
using Deck.Core.Servers;

namespace Deck.App;

/// <summary>One row of the test checklist, ready for the UI.</summary>
public sealed class TestStepView(TestStep step)
{
    public string Name { get; } = step.Name;

    public string? Detail { get; } = step.Detail;

    public string Glyph { get; } = step.Status switch
    {
        TestStepStatus.Passed => "✓",
        TestStepStatus.Failed => "✕",
        TestStepStatus.Running => "…",
        TestStepStatus.Skipped => "–",
        _ => "·",
    };

    public Brush Brush { get; } = step.Status switch
    {
        TestStepStatus.Passed => Resource("OkBrush"),
        TestStepStatus.Failed => Resource("BadBrush"),
        TestStepStatus.Running => Resource("AccentBrush"),
        _ => Resource("MutedTextBrush"),
    };

    private static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public sealed class ServerEditorViewModel : ObservableObject
{
    private string _pasteText = string.Empty;
    private string _pasteFeedback = string.Empty;
    private string _testSummary = string.Empty;
    private string? _testAdvice;
    private bool _isTesting;
    private bool _showAdvanced;
    private bool _showServerTypeOverride;
    private QualityPreset? _selectedPreset;
    private HostPreset? _selectedHostPreset;

    public ServerEditorViewModel(ServerProfile profile)
    {
        Profile = profile;
        _selectedPreset = QualityPreset.Match(profile.Encoder) ?? QualityPreset.Default;
        _showAdvanced = QualityPreset.Match(profile.Encoder) is null;

        // Grouped so the one question can hold three kinds of answer - the company, the software,
        // or "I don't know" - without reading as a jumble.
        HostPresetsView = new CollectionViewSource { Source = HostPreset.All }.View;
        HostPresetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HostPreset.Group)));

        // Set directly rather than through the property: selecting a preset applies it, and an
        // existing server being edited must not have its settings rewritten just by opening it.
        if (string.IsNullOrWhiteSpace(profile.Host)) _selectedHostPreset = HostPreset.Generic;
    }

    public ServerProfile Profile { get; }

    // ---------------------------------------------------------------- host preset (C11)

    public ICollectionView HostPresetsView { get; }

    public HostPreset? SelectedHostPreset
    {
        get => _selectedHostPreset;
        set
        {
            if (!Set(ref _selectedHostPreset, value) || value is null) return;

            value.ApplyTo(Profile);

            RaiseAll(nameof(ServerTypeValue), nameof(Port), nameof(UseTls), nameof(Username),
                nameof(StreamPathLabel), nameof(StreamPathHint), nameof(ShowMountPoint),
                nameof(ShowStreamId), nameof(ShowUsername), nameof(ListenUrl),
                nameof(HostGuidance), nameof(HostFieldNaming), nameof(ShowHostPlaceholder),
                nameof(ServerTypeSummary), nameof(ShowServerTypeOverride), nameof(ShowServerTypeLink));
        }
    }

    public string? HostGuidance => _selectedHostPreset?.WhereToFind
        ?? "Choose a host only if you want Deck to fill in its standard settings again.";

    /// <summary>
    /// An existing server has no preset selected - there is no way to know afterwards which host it
    /// came from - and an empty picker at the top of a form reads as a required question nobody has
    /// answered. So it says what it is instead of sitting there blank.
    /// </summary>
    public bool ShowHostPlaceholder => _selectedHostPreset is null;

    public string? HostFieldNaming => _selectedHostPreset?.FieldNaming;

    // ---------------------------------------------------------------- server type (C3)

    /// <summary>What the host question decided, said plainly, since the picker itself is hidden.</summary>
    public string ServerTypeSummary => Profile.ServerType.ConnectionSummary();

    /// <summary>
    /// The raw picker. Hidden by default: for every host in the list, answering "who hosts your
    /// stream?" has already answered this, and asking the same thing twice in different words is the
    /// single most confusing part of setting a server up.
    /// <para>
    /// It appears on request, and by itself the moment the type disagrees with the chosen host - an
    /// override the user cannot see is one they cannot undo.
    /// </para>
    /// </summary>
    public bool ShowServerTypeOverride =>
        _showServerTypeOverride || (_selectedHostPreset?.Contradicts(Profile.ServerType) ?? false);

    public bool ShowServerTypeLink => !ShowServerTypeOverride;

    public void RevealServerTypeOverride()
    {
        _showServerTypeOverride = true;
        RaiseAll(nameof(ShowServerTypeOverride), nameof(ShowServerTypeLink));
    }

    // ---------------------------------------------------------------- paste a URL (C2)

    public string PasteText
    {
        get => _pasteText;
        set => Set(ref _pasteText, value);
    }

    public string PasteFeedback
    {
        get => _pasteFeedback;
        private set => Set(ref _pasteFeedback, value);
    }

    public Brush PasteFeedbackBrush { get; private set; } = Brushes.Gray;

    /// <summary>
    /// Fills the form from whatever the user pasted. Anything it recognises is listed back to them,
    /// so it is obvious what was understood and what still needs typing.
    /// </summary>
    public void ApplyPaste()
    {
        var result = StreamUrlParser.Parse(PasteText);

        if (!result.Success || result.Profile is null)
        {
            PasteFeedback = result.Message ?? "Deck could not make sense of that.";
            PasteFeedbackBrush = Resource("BadBrush");
            RaiseAll(nameof(PasteFeedback), nameof(PasteFeedbackBrush));
            return;
        }

        var parsed = result.Profile;

        Profile.Host = parsed.Host;
        Profile.Port = parsed.Port;
        Profile.UseTls = parsed.UseTls;
        if (parsed.NormalisedMount != "/") Profile.MountPoint = parsed.MountPoint;
        if (!string.IsNullOrWhiteSpace(parsed.Username)) Profile.Username = parsed.Username;
        if (!string.IsNullOrEmpty(parsed.Password)) Profile.Password = parsed.Password;
        if (parsed.ServerType != ServerType.Unknown) Profile.ServerType = parsed.ServerType;
        if (parsed.StreamId != 1) Profile.StreamId = parsed.StreamId;
        if (!string.IsNullOrWhiteSpace(parsed.StationName)) Profile.StationName = parsed.StationName;
        if (!string.IsNullOrWhiteSpace(parsed.Genre)) Profile.Genre = parsed.Genre;
        if (!string.IsNullOrWhiteSpace(parsed.Website)) Profile.Website = parsed.Website;
        if (string.IsNullOrWhiteSpace(Profile.Name) || Profile.Name == "New server") Profile.Name = parsed.Name;

        PasteFeedback = $"Filled in: {string.Join(", ", result.Recognised)}. Check the rest, then press Test.";
        PasteFeedbackBrush = Resource("OkBrush");

        RaiseAll(
            nameof(PasteFeedback), nameof(PasteFeedbackBrush), nameof(Host), nameof(Port), nameof(UseTls),
            nameof(MountPoint), nameof(Username), nameof(Password), nameof(ServerTypeValue), nameof(StreamId),
            nameof(Name), nameof(StationName), nameof(Genre), nameof(Website),
            nameof(StreamPathLabel), nameof(StreamPathHint), nameof(ShowMountPoint), nameof(ShowStreamId),
            nameof(ShowUsername), nameof(ListenUrl),
            nameof(ServerTypeSummary), nameof(ShowServerTypeOverride), nameof(ShowServerTypeLink));
    }

    // ---------------------------------------------------------------- fields

    public string Name
    {
        get => Profile.Name;
        set { Profile.Name = value; Raise(); }
    }

    public ServerType ServerTypeValue
    {
        get => Profile.ServerType;
        set
        {
            Profile.ServerType = value;
            RaiseAll(nameof(ServerTypeValue), nameof(StreamPathLabel), nameof(StreamPathHint),
                nameof(ShowMountPoint), nameof(ShowStreamId), nameof(ShowUsername), nameof(ListenUrl),
                nameof(ServerTypeSummary), nameof(ShowServerTypeOverride), nameof(ShowServerTypeLink));
        }
    }

    public IReadOnlyList<ServerType> ServerTypes { get; } =
        [ServerType.Unknown, ServerType.Icecast, ServerType.ShoutcastV2, ServerType.ShoutcastV1];

    public string Host
    {
        get => Profile.Host;
        set { Profile.Host = value.Trim(); RaiseAll(nameof(Host), nameof(ListenUrl)); }
    }

    public int Port
    {
        get => Profile.Port;
        set { Profile.Port = value; RaiseAll(nameof(Port), nameof(ListenUrl)); }
    }

    public bool UseTls
    {
        get => Profile.UseTls;
        set { Profile.UseTls = value; RaiseAll(nameof(UseTls), nameof(ListenUrl)); }
    }

    public string MountPoint
    {
        get => Profile.MountPoint;
        set { Profile.MountPoint = value; RaiseAll(nameof(MountPoint), nameof(ListenUrl)); }
    }

    public int StreamId
    {
        get => Profile.StreamId;
        set { Profile.StreamId = value; RaiseAll(nameof(StreamId), nameof(ListenUrl)); }
    }

    public string Username
    {
        get => Profile.Username;
        set { Profile.Username = value; Raise(); }
    }

    public string Password
    {
        get => Profile.Password ?? string.Empty;
        set { Profile.Password = value; Raise(); }
    }

    public string StationName
    {
        get => Profile.StationName;
        set { Profile.StationName = value; Raise(); }
    }

    public string Description
    {
        get => Profile.Description;
        set { Profile.Description = value; Raise(); }
    }

    public string Genre
    {
        get => Profile.Genre;
        set { Profile.Genre = value; Raise(); }
    }

    public string Website
    {
        get => Profile.Website;
        set { Profile.Website = value; Raise(); }
    }

    public bool ListInDirectory
    {
        get => Profile.ListInDirectory;
        set { Profile.ListInDirectory = value; Raise(); }
    }

    // Labels follow the server type so the user reads the word their host used.
    public string StreamPathLabel => Profile.ServerType.StreamPathLabel();

    public string StreamPathHint => Profile.ServerType.StreamPathHint();

    public bool ShowMountPoint => Profile.ServerType is ServerType.Icecast or ServerType.Unknown;

    public bool ShowStreamId => Profile.ServerType.UsesStreamId();

    public bool ShowUsername => Profile.ServerType is ServerType.Icecast or ServerType.Unknown;

    public string ListenUrl => string.IsNullOrWhiteSpace(Profile.Host) ? string.Empty : Profile.ListenUrl;

    // ---------------------------------------------------------------- quality (D5, D6)

    public IReadOnlyList<QualityPreset> Presets { get; } = QualityPreset.All;

    public QualityPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value) || value is null) return;

            Profile.Encoder = value.Settings;
            RaiseAll(nameof(Codec), nameof(BitrateKbps), nameof(SampleRate), nameof(Channels),
                nameof(QualityDescription), nameof(Bitrates), nameof(SampleRates));
        }
    }

    public string QualityDescription => _selectedPreset is null
        ? Profile.Encoder.Summary
        : $"{_selectedPreset.Description} {QualityPreset.BandwidthPerListener(Profile.Encoder)}.";

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set => Set(ref _showAdvanced, value);
    }

    public IReadOnlyList<StreamCodec> Codecs { get; } =
        [StreamCodec.Mp3, StreamCodec.OggOpus, StreamCodec.OggVorbis, StreamCodec.OggFlac];

    public StreamCodec Codec
    {
        get => Profile.Encoder.Codec;
        set
        {
            Profile.Encoder = (Profile.Encoder with { Codec = value }).Normalised();
            _selectedPreset = QualityPreset.Match(Profile.Encoder);
            RaiseAll(nameof(Codec), nameof(CodecBlurb), nameof(Bitrates), nameof(SampleRates),
                nameof(BitrateKbps), nameof(SampleRate), nameof(SelectedPreset),
                nameof(QualityDescription), nameof(ShowBitrate), nameof(LosslessNote));
        }
    }

    public string CodecBlurb => Profile.Encoder.Codec.Blurb();

    /// <summary>Lossless has no bitrate to choose, so the picker is replaced by what it will cost.</summary>
    public bool ShowBitrate => !Profile.Encoder.Codec.IsLossless();

    public string? LosslessNote => Profile.Encoder.Codec.IsLossless()
        ? $"Nothing is thrown away, so there is no quality to set. Expect around " +
          $"{Profile.Encoder.EstimatedLosslessKbps} kbps per listener — roughly six times an MP3 stream. " +
          "Check your host allows it before going live."
        : null;

    public IReadOnlyList<int> Bitrates => EncoderSettings.AvailableBitrates(Profile.Encoder.Codec);

    public IReadOnlyList<int> SampleRates => EncoderSettings.AvailableSampleRates(Profile.Encoder.Codec);

    public int BitrateKbps
    {
        get => Profile.Encoder.BitrateKbps;
        set
        {
            Profile.Encoder = Profile.Encoder with { BitrateKbps = value };
            _selectedPreset = QualityPreset.Match(Profile.Encoder);
            RaiseAll(nameof(BitrateKbps), nameof(SelectedPreset), nameof(QualityDescription));
        }
    }

    public int SampleRate
    {
        get => Profile.Encoder.SampleRate;
        set
        {
            Profile.Encoder = Profile.Encoder with { SampleRate = value };
            _selectedPreset = QualityPreset.Match(Profile.Encoder);
            RaiseAll(nameof(SampleRate), nameof(SelectedPreset), nameof(QualityDescription));
        }
    }

    public bool IsStereo
    {
        get => Profile.Encoder.Channels == 2;
        set
        {
            Profile.Encoder = Profile.Encoder with { Channels = value ? 2 : 1 };
            _selectedPreset = QualityPreset.Match(Profile.Encoder);
            RaiseAll(nameof(IsStereo), nameof(Channels), nameof(SelectedPreset), nameof(QualityDescription));
        }
    }

    public int Channels => Profile.Encoder.Channels;

    // ---------------------------------------------------------------- test (B6, C7)

    public ObservableCollection<TestStepView> TestSteps { get; } = [];

    public bool IsTesting
    {
        get => _isTesting;
        private set => Set(ref _isTesting, value);
    }

    public string TestSummary
    {
        get => _testSummary;
        private set => Set(ref _testSummary, value);
    }

    public string? TestAdvice
    {
        get => _testAdvice;
        private set => Set(ref _testAdvice, value);
    }

    public Brush TestSummaryBrush { get; private set; } = Brushes.Gray;

    public async Task TestAsync()
    {
        var problems = Profile.Validate();
        if (problems.Count > 0)
        {
            TestSummary = problems[0];
            TestSummaryBrush = Resource("BadBrush");
            TestAdvice = null;
            RaiseAll(nameof(TestSummary), nameof(TestSummaryBrush));
            return;
        }

        IsTesting = true;
        TestSummary = "Testing…";
        TestAdvice = null;
        TestSummaryBrush = Resource("MutedTextBrush");
        RaiseAll(nameof(TestSummary), nameof(TestSummaryBrush));

        var progress = new Progress<IReadOnlyList<TestStep>>(steps =>
        {
            TestSteps.Clear();
            foreach (var step in steps) TestSteps.Add(new TestStepView(step));
        });

        try
        {
            var result = await new ConnectionTester().RunAsync(Profile, progress).ConfigureAwait(true);

            // The tester may have identified the server for us; keep that so the user does not have
            // to answer a question Deck already knows the answer to.
            if (result.DetectedType != ServerType.Unknown && Profile.ServerType != result.DetectedType)
            {
                ServerTypeValue = result.DetectedType;
            }

            TestSummary = result.Summary;
            TestAdvice = result.Advice;
            TestSummaryBrush = Resource(result.Success ? "OkBrush" : "BadBrush");
        }
        catch (Exception ex)
        {
            TestSummary = ex.Message;
            TestAdvice = null;
            TestSummaryBrush = Resource("BadBrush");
        }
        finally
        {
            IsTesting = false;
            RaiseAll(nameof(TestSummary), nameof(TestAdvice), nameof(TestSummaryBrush));
        }
    }

    public IReadOnlyList<string> Validate() => Profile.Validate();

    private static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
