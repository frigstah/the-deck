namespace Deck.Core.Codecs;

public static class EncoderFactory
{
    public static IAudioEncoder Create(EncoderSettings settings) => settings.Codec switch
    {
        StreamCodec.Mp3 => new Mp3Encoder(settings),
        StreamCodec.OggOpus => new OpusEncoder(settings),
        StreamCodec.OggVorbis => new VorbisEncoder(settings),
        StreamCodec.OggFlac => new FlacEncoder(settings),
        _ => throw new NotSupportedException($"No encoder for {settings.Codec}."),
    };
}
