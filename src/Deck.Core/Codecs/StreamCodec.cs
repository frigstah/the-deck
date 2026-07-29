namespace Deck.Core.Codecs;

public enum StreamCodec
{
    Mp3,
    OggOpus,
    OggVorbis,
    OggFlac,
}

public static class StreamCodecInfo
{
    /// <summary>What the user sees in the codec picker.</summary>
    public static string DisplayName(this StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => "MP3",
        StreamCodec.OggOpus => "Opus",
        StreamCodec.OggVorbis => "Vorbis",
        StreamCodec.OggFlac => "FLAC",
        _ => codec.ToString(),
    };

    /// <summary>True when the codec throws nothing away, so bitrate is a result rather than a setting.</summary>
    public static bool IsLossless(this StreamCodec codec) => codec == StreamCodec.OggFlac;

    /// <summary>One line explaining the trade-off, so nobody has to research it.</summary>
    public static string Blurb(this StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => "Plays everywhere. The safe choice if you are not sure.",
        StreamCodec.OggOpus => "Sounds better at low bitrates. Not supported by some older players.",
        StreamCodec.OggVorbis => "Better than MP3, and older than Opus. Use it if your host asks for it.",
        StreamCodec.OggFlac => "Loses nothing at all, and costs roughly six times the bandwidth. For archives and small, close audiences.",
        _ => string.Empty,
    };

    public static string ContentType(this StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => "audio/mpeg",
        StreamCodec.OggOpus or StreamCodec.OggVorbis or StreamCodec.OggFlac => "audio/ogg",
        _ => "application/octet-stream",
    };

    /// <summary>File extension used for recordings in the same codec as the stream.</summary>
    public static string FileExtension(this StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => ".mp3",
        StreamCodec.OggOpus => ".opus",
        StreamCodec.OggVorbis => ".ogg",

        // .oga, not .flac: the bytes are FLAC inside an Ogg container, and a player handed a .flac
        // file that turns out to be Ogg will usually refuse it.
        StreamCodec.OggFlac => ".oga",
        _ => ".bin",
    };

    /// <summary>The format string SHOUTcast v2 and some Icecast setups expect.</summary>
    public static string ShoutcastFormatName(this StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => "audio/mpeg",
        StreamCodec.OggOpus or StreamCodec.OggVorbis or StreamCodec.OggFlac => "audio/ogg",
        _ => "audio/mpeg",
    };
}
