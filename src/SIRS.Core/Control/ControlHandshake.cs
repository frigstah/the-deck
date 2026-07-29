using System.Text.Json;
using Sirs.Core.Servers;

namespace Sirs.Core.Control;

/// <summary>
/// How <c>SIRS.exe --live</c> finds the copy of SIRS already running (I10).
/// <para>
/// The running instance drops its port and password here while the control endpoint is up, and
/// removes the file when it stops. A second copy launched with a command-line switch reads it,
/// sends the command to the first, and exits without ever opening a window.
/// </para>
/// <para>
/// The password is encrypted with DPAPI, like server passwords are. It is only ever read by the same
/// user on the same machine, so nothing is lost by it - but this file sits in roaming application
/// data, which on a domain account follows the user onto other computers, and a password that
/// travels in plain text because nobody thought about it is exactly how these things go wrong.
/// </para>
/// </summary>
public static class ControlHandshake
{
    private sealed record Handshake(int Port, string? Token, int ProcessId);

    public static void Write(int port, string? token)
    {
        try
        {
            var handshake = new Handshake(port, SecretProtector.Protect(token), Environment.ProcessId);
            File.WriteAllText(AppPaths.ControlFile, JsonSerializer.Serialize(handshake));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The endpoint still works; only the command line loses the shortcut of finding it.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.ControlFile)) File.Delete(AppPaths.ControlFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale file is handled by the reader, which checks the process is still alive.
        }
    }

    /// <summary>
    /// Where the running SIRS is listening, or null if none is. A file left behind by a crash names
    /// a process that no longer exists, so the id is checked rather than trusted - otherwise every
    /// command after a crash would hang trying to reach a port nobody is on.
    /// </summary>
    public static (int Port, string? Token)? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.ControlFile)) return null;

            var handshake = JsonSerializer.Deserialize<Handshake>(File.ReadAllText(AppPaths.ControlFile));
            if (handshake is null || handshake.Port <= 0) return null;

            if (!IsAlive(handshake.ProcessId)) return null;

            return (handshake.Port, SecretProtector.Unprotect(handshake.Token));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAlive(int processId)
    {
        if (processId <= 0) return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
