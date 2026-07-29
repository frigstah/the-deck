namespace Deck.Core.Audio;

/// <summary>Sample rate / channel-count pair used throughout the pipeline.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public static readonly AudioFormat CdStereo = new(44100, 2);

    public bool IsStereo => Channels == 2;

    public int BytesPerSecondFloat => SampleRate * Channels * sizeof(float);

    public override string ToString() =>
        $"{SampleRate / 1000.0:0.#} kHz {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + "ch")}";
}
