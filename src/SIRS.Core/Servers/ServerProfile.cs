using System.Text.Json.Serialization;
using Sirs.Core.Codecs;
using Sirs.Core.Localisation;

namespace Sirs.Core.Servers;

/// <summary>
/// One saved destination (C1). Passwords live in <see cref="ProtectedPassword"/> on disk; the
/// plain value is only ever held in memory via <see cref="Password"/>.
/// </summary>
public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the user calls it, e.g. "My Station" or "Backup relay".</summary>
    public string Name { get; set; } = "New server";

    public ServerType ServerType { get; set; } = ServerType.Unknown;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 8000;

    public bool UseTls { get; set; }

    /// <summary>Icecast mount point, stored with a leading slash.</summary>
    public string MountPoint { get; set; } = "/stream";

    /// <summary>SHOUTcast v2 stream id. Ignored by the other server types.</summary>
    public int StreamId { get; set; } = 1;

    public string Username { get; set; } = "source";

    [JsonPropertyName("password")]
    public string? ProtectedPassword { get; set; }

    /// <summary>Plain-text password. Never serialised - only the protected form is written.</summary>
    [JsonIgnore]
    public string? Password
    {
        get => SecretProtector.Unprotect(ProtectedPassword);
        set => ProtectedPassword = SecretProtector.Protect(value);
    }

    // Public listing details (C8).
    public string StationName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string Website { get; set; } = string.Empty;

    /// <summary>Whether the server should advertise this stream in public directories.</summary>
    public bool ListInDirectory { get; set; }

    public EncoderSettings Encoder { get; set; } = QualityPreset.Default.Settings;

    /// <summary>The URL a listener would open. Used by "Listen to my stream" and shown in the editor.</summary>
    [JsonIgnore]
    public string ListenUrl
    {
        get
        {
            var scheme = UseTls ? "https" : "http";
            var authority = IsDefaultPort ? Host : $"{Host}:{Port}";
            return ServerType switch
            {
                ServerType.Icecast => $"{scheme}://{authority}{NormalisedMount}",
                ServerType.ShoutcastV2 => $"{scheme}://{authority}/stream/{StreamId}/",
                _ => $"{scheme}://{authority}/;",
            };
        }
    }

    [JsonIgnore]
    private bool IsDefaultPort => (UseTls && Port == 443) || (!UseTls && Port == 80);

    /// <summary>Mount point with exactly one leading slash, however the user typed it.</summary>
    [JsonIgnore]
    public string NormalisedMount
    {
        get
        {
            var mount = MountPoint?.Trim() ?? string.Empty;
            if (mount.Length == 0) return "/";
            return mount.StartsWith('/') ? mount : "/" + mount;
        }
    }

    /// <summary>One-line summary for the server list, e.g. "Icecast - stream.example.com:8000/live".</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var path = ServerType switch
            {
                ServerType.Icecast => NormalisedMount,
                ServerType.ShoutcastV2 => $" (stream {StreamId})",
                _ => string.Empty,
            };
            var type = ServerType == ServerType.Unknown ? "Not detected yet" : ServerType.DisplayName();
            return $"{type} — {Host}:{Port}{path}";
        }
    }

    /// <summary>
    /// Problems that would stop a connection, phrased for the user. Empty means ready to try.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name)) problems.Add(Strings.Get(StringId.ServerNeedsName));
        if (string.IsNullOrWhiteSpace(Host)) problems.Add(Strings.Get(StringId.ServerNeedsHost));
        if (Port is < 1 or > 65535) problems.Add(Strings.Get(StringId.ServerNeedsPort));
        if (string.IsNullOrEmpty(Password)) problems.Add(Strings.Get(StringId.ServerNeedsPassword));

        if (ServerType.UsesMountPoint() && NormalisedMount == "/")
        {
            problems.Add(Strings.Get(StringId.ServerNeedsMount));
        }

        if (ServerType.UsesUsername() && string.IsNullOrWhiteSpace(Username))
        {
            problems.Add(Strings.Get(StringId.ServerNeedsUsername));
        }

        return problems;
    }

    public ServerProfile Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        ServerType = ServerType,
        Host = Host,
        Port = Port,
        UseTls = UseTls,
        MountPoint = MountPoint,
        StreamId = StreamId,
        Username = Username,
        ProtectedPassword = ProtectedPassword,
        StationName = StationName,
        Description = Description,
        Genre = Genre,
        Website = Website,
        ListInDirectory = ListInDirectory,
        Encoder = Encoder,
    };
}
