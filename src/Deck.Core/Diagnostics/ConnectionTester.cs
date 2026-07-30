using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Deck.Core.Codecs;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.Core.Diagnostics;

/// <summary>
/// The Test Connection feature (B6, C7). Runs the handshake one visible stage at a time so a
/// failure points at the field that caused it, instead of the single "connection failed" that
/// other encoders give you.
/// <para>
/// The last stage really does push encoded audio at the server, because a handshake that succeeds
/// proves nothing about whether the server will accept the chosen format.
/// </para>
/// </summary>
public sealed class ConnectionTester
{
    private static readonly TimeSpan StreamTestDuration = TimeSpan.FromSeconds(2);

    public async Task<ConnectionTestResult> RunAsync(
        ServerProfile profile,
        IProgress<IReadOnlyList<TestStep>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<TestStep>
        {
            new(ConnectionTestStage.ResolveAddress, "Find the server"),
            new(ConnectionTestStage.OpenConnection, "Open a connection"),
            new(ConnectionTestStage.SecureConnection, "Secure the connection"),
            new(ConnectionTestStage.IdentifyServer, "Identify the server"),
            new(ConnectionTestStage.SignIn, "Sign in"),
            new(ConnectionTestStage.SendAudio, "Send a few seconds of audio"),
        };

        void Report() => progress?.Report(steps.ToList());

        void Update(ConnectionTestStage stage, TestStepStatus status, string? detail = null)
        {
            var index = steps.FindIndex(s => s.Stage == stage);
            steps[index] = steps[index] with { Status = status, Detail = detail };
            Report();
        }

        ConnectionTestResult Fail(string summary, string? advice) =>
            new(false, steps, summary, advice, profile.ServerType);

        Report();

        // 1. DNS -------------------------------------------------------------------------------
        Update(ConnectionTestStage.ResolveAddress, TestStepStatus.Running);
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(profile.Host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

            Update(ConnectionTestStage.ResolveAddress, TestStepStatus.Passed, $"{profile.Host} is at {addresses[0]}");
        }
        catch (Exception ex)
        {
            var detail = ex is SocketException { SocketErrorCode: SocketError.HostNotFound }
                ? $"There is no server called \"{profile.Host}\"."
                : "Could not look up that address. Check your internet connection.";

            Update(ConnectionTestStage.ResolveAddress, TestStepStatus.Failed, detail);
            MarkRemainingSkipped(steps, ConnectionTestStage.ResolveAddress, Report);
            return Fail(detail, "Check the server address for a typo. It usually looks like stream.yourhost.com.");
        }

        // 2. TCP -------------------------------------------------------------------------------
        Update(ConnectionTestStage.OpenConnection, TestStepStatus.Running);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(profile.Host, profile.Port, timeout.Token).ConfigureAwait(false);
            Update(ConnectionTestStage.OpenConnection, TestStepStatus.Passed,
                $"Port {profile.Port} answered in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            var detail = SourceConnection.Translate(ex, profile.Host, profile.Port, profile.UseTls).Message;
            Update(ConnectionTestStage.OpenConnection, TestStepStatus.Failed, detail);
            MarkRemainingSkipped(steps, ConnectionTestStage.OpenConnection, Report);
            return Fail(detail, "Check the port number. If it looks right, a firewall or your internet provider may be blocking it.");
        }

        // 3. TLS -------------------------------------------------------------------------------
        if (!profile.UseTls)
        {
            Update(ConnectionTestStage.SecureConnection, TestStepStatus.Skipped, "Not using a secure connection");
        }
        else
        {
            Update(ConnectionTestStage.SecureConnection, TestStepStatus.Running);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(profile.Host, profile.Port, cancellationToken).ConfigureAwait(false);

                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = profile.Host },
                    cancellationToken).ConfigureAwait(false);

                Update(ConnectionTestStage.SecureConnection, TestStepStatus.Passed,
                    $"Secured with {ssl.SslProtocol}");
            }
            catch (Exception ex)
            {
                var detail = SourceConnection.Translate(ex, profile.Host, profile.Port, true).Message;
                Update(ConnectionTestStage.SecureConnection, TestStepStatus.Failed, detail);
                MarkRemainingSkipped(steps, ConnectionTestStage.SecureConnection, Report);
                return Fail(detail, "If your host did not tell you to use a secure connection, turn that option off and test again.");
            }
        }

        // 4. Identify --------------------------------------------------------------------------
        Update(ConnectionTestStage.IdentifyServer, TestStepStatus.Running);
        var effectiveProfile = profile;
        var probe = await ServerProbe.DetectAsync(profile.Host, profile.Port, profile.UseTls, cancellationToken)
            .ConfigureAwait(false);

        if (probe.DetectedType != ServerType.Unknown)
        {
            if (profile.ServerType == ServerType.Unknown)
            {
                // Auto-detection (C3): adopt what the probe found so the rest of the test - and the
                // saved profile - use the right protocol.
                effectiveProfile = profile.Clone();
                effectiveProfile.Id = profile.Id;
                effectiveProfile.ServerType = probe.DetectedType;

                Update(ConnectionTestStage.IdentifyServer, TestStepStatus.Passed,
                    $"{probe.DetectedType.DisplayName()} — Deck filled this in for you");
            }
            else if (probe.DetectedType != profile.ServerType)
            {
                effectiveProfile = profile.Clone();
                effectiveProfile.Id = profile.Id;
                effectiveProfile.ServerType = probe.DetectedType;

                Update(ConnectionTestStage.IdentifyServer, TestStepStatus.Passed,
                    $"This is {probe.DetectedType.DisplayName()}, not {profile.ServerType.DisplayName()} — Deck corrected it");
            }
            else
            {
                Update(ConnectionTestStage.IdentifyServer, TestStepStatus.Passed, probe.DetectedType.DisplayName());
            }
        }
        else if (profile.ServerType == ServerType.Unknown)
        {
            const string detail = "Deck could not tell what kind of server this is.";
            Update(ConnectionTestStage.IdentifyServer, TestStepStatus.Failed, detail);
            MarkRemainingSkipped(steps, ConnectionTestStage.IdentifyServer, Report);
            return Fail(detail, "Choose the server type your host told you to use, then test again.");
        }
        else
        {
            Update(ConnectionTestStage.IdentifyServer, TestStepStatus.Skipped,
                $"Could not confirm the type; carrying on as {profile.ServerType.DisplayName()}");
        }

        // 5 and 6. Sign in and stream ----------------------------------------------------------
        Update(ConnectionTestStage.SignIn, TestStepStatus.Running);

        var encoderSettings = effectiveProfile.Encoder.Normalised();
        IStreamSink? sink = null;
        IAudioEncoder? encoder = null;

        try
        {
            encoder = EncoderFactory.Create(encoderSettings);
            sink = SinkFactory.Create(effectiveProfile, encoderSettings);

            await sink.ConnectAsync(cancellationToken).ConfigureAwait(false);
            Update(ConnectionTestStage.SignIn, TestStepStatus.Passed, sink.ConnectionNote ?? "The server accepted the password");

            Update(ConnectionTestStage.SendAudio, TestStepStatus.Running);

            if (encoder.StreamHeader.Length > 0)
            {
                await sink.SendAsync(encoder.StreamHeader, cancellationToken).ConfigureAwait(false);
            }

            var sent = await SendTestAudioAsync(sink, encoder, encoderSettings, cancellationToken).ConfigureAwait(false);
            Update(ConnectionTestStage.SendAudio, TestStepStatus.Passed,
                $"The server accepted {encoderSettings.Summary} ({sent:N0} bytes)");
        }
        catch (StreamException ex)
        {
            var stage = steps.First(s => s.Stage == ConnectionTestStage.SignIn).Status == TestStepStatus.Passed
                ? ConnectionTestStage.SendAudio
                : ConnectionTestStage.SignIn;

            Update(stage, TestStepStatus.Failed, ex.Message);
            MarkRemainingSkipped(steps, stage, Report);

            return new ConnectionTestResult(
                false, steps, ex.Message,
                AdviceFor(ex, effectiveProfile, signedIn: stage == ConnectionTestStage.SendAudio),
                effectiveProfile.ServerType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var stage = steps.First(s => s.Stage == ConnectionTestStage.SignIn).Status == TestStepStatus.Passed
                ? ConnectionTestStage.SendAudio
                : ConnectionTestStage.SignIn;

            Update(stage, TestStepStatus.Failed, ex.Message);
            MarkRemainingSkipped(steps, stage, Report);
            return Fail(ex.Message, null);
        }
        finally
        {
            if (sink is not null) await sink.DisposeAsync().ConfigureAwait(false);
            encoder?.Dispose();
        }

        return new ConnectionTestResult(
            true,
            steps,
            $"\"{effectiveProfile.Name}\" is ready to broadcast.",
            null,
            effectiveProfile.ServerType);
    }

    /// <summary>
    /// Pushes real encoded audio - silence, so nothing embarrassing goes out if a listener happens
    /// to be connected - to prove the server accepts the format and not just the password.
    /// </summary>
    private static async Task<int> SendTestAudioAsync(
        IStreamSink sink,
        IAudioEncoder encoder,
        EncoderSettings settings,
        CancellationToken cancellationToken)
    {
        var blockFrames = settings.SampleRate / 100; // 10 ms
        var silence = new float[blockFrames * settings.Channels];
        var blocks = (int)(StreamTestDuration.TotalMilliseconds / 10);
        var sent = 0;

        for (var i = 0; i < blocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Copied out in a synchronous helper: Encode returns a span, which cannot live across
            // an await.
            var block = EncodeBlock(encoder, silence);
            if (block.Length > 0)
            {
                await sink.SendAsync(block, cancellationToken).ConfigureAwait(false);
                sent += block.Length;
            }

            // Roughly real time: blasting two seconds of audio instantly would not exercise the
            // server the way a real broadcast does.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    private static byte[] EncodeBlock(IAudioEncoder encoder, float[] samples) => encoder.Encode(samples).ToArray();

    private static string? AdviceFor(StreamException ex, ServerProfile profile, bool signedIn) => ex.Failure switch
    {
        // Signed in, then dropped without a word. Deck used to leave the user with "the connection to
        // the server was lost", which sends them to look at their internet - and it is not the network,
        // it is the server objecting to the broadcast. The station name leads the list because a
        // SHOUTcast server refuses a nameless one exactly like this, silently, and it is the single most
        // likely cause on a profile that was set up by pasting a host's email.
        StreamFailure.Network when signedIn && profile.ServerType.NeedsStationName()
                                  && string.IsNullOrWhiteSpace(profile.StationName) =>
            "Give the station a name under \"What listeners see\" and test again. SHOUTcast accepts the " +
            "password and then closes the connection when the broadcast has no name, without saying that " +
            "is the reason.",

        StreamFailure.Network =>
            "The sign-in worked, so the address, port and password are right. A server that then drops the " +
            "broadcast is refusing the audio: check the bitrate is one this stream allows, and that nothing " +
            "else is already broadcasting to it.",

        StreamFailure.Authentication =>
            "Copy the password straight from your host's email — a trailing space is the usual culprit.",

        StreamFailure.MountInUse =>
            "Another encoder is already connected. Close it, or wait a moment and test again.",

        StreamFailure.FormatRejected =>
            $"Open the quality settings and choose a different format. {StreamCodec.Mp3.DisplayName()} is accepted almost everywhere.",

        StreamFailure.Tls =>
            "Turn off the secure connection option unless your host specifically asked for it.",

        StreamFailure.Protocol when profile.ServerType == ServerType.Icecast =>
            "Check the stream address. Your host will have given you something like /live or /stream.",

        _ => null,
    };

    private static void MarkRemainingSkipped(List<TestStep> steps, ConnectionTestStage after, Action report)
    {
        var index = steps.FindIndex(s => s.Stage == after);
        for (var i = index + 1; i < steps.Count; i++)
        {
            if (steps[i].Status == TestStepStatus.Pending)
            {
                steps[i] = steps[i] with { Status = TestStepStatus.Skipped, Detail = "Not reached" };
            }
        }

        report();
    }
}
