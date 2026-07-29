namespace Sirs.Core.Control;

/// <summary>What came back from a command sent to a running SIRS.</summary>
public readonly record struct ControlReply(bool Ok, string Text);

/// <summary>
/// Sends a command line to the SIRS already running (I10).
/// <para>
/// Always to loopback, never to a host the caller names. This is the command line of the program on
/// this machine talking to itself; giving it the ability to aim at another computer would turn
/// <c>SIRS.exe</c> into a small tool for putting other people's stations off air, which is not a
/// feature anyone asked for.
/// </para>
/// </summary>
public static class ControlClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<ControlReply> SendAsync(CommandLineRequest request)
    {
        if (request.Error is { } error) return new ControlReply(false, $"{error}\n\n{CommandLine.HelpText}");

        if (request.IsHelp) return new ControlReply(true, CommandLine.HelpText);

        if (ControlHandshake.Read() is not { } handshake)
        {
            return new ControlReply(false,
                "SIRS is not running, or its remote control is switched off.\n" +
                "Open SIRS, and under \"SIRS itself\" turn on \"Let other programs control SIRS\".");
        }

        var (port, token) = handshake;

        var separator = request.Path.Contains('?') ? '&' : '?';
        var query = string.IsNullOrEmpty(token)
            ? string.Empty
            : $"{separator}token={Uri.EscapeDataString(token)}";

        using var client = new HttpClient { Timeout = Timeout };

        try
        {
            using var response = await client
                .GetAsync($"http://127.0.0.1:{port}{request.Path}{query}")
                .ConfigureAwait(false);

            var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).TrimEnd();

            return new ControlReply(response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ControlReply(false, $"SIRS did not answer on port {port}: {ex.Message}");
        }
    }
}
