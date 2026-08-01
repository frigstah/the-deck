using Deck.Core;
using Deck.Core.Codecs;

namespace Deck.EncoderCheck;

/// <summary>
/// One sample rate for the whole of Deck (D5), and the upgrade that got it there.
/// <para>
/// The rate used to belong to each server. Moving it to a single setting under Sound is the kind of
/// change that is easy to get right for a new install and easy to get wrong for everybody who
/// already had one - which is what most of this file is about.
/// </para>
/// </summary>
internal static class SampleRateChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("a stored choice is never second-guessed", () =>
        {
            // Whatever the servers say, a rate somebody has chosen is the answer. The servers are
            // only consulted when there is nothing stored at all.
            var chosen = EncoderSettings.ResolveSampleRate(48000, [22050, 22050, 22050]);
            Expect(chosen == 48000, $"a stored 48 kHz came back as {chosen}");

            var low = EncoderSettings.ResolveSampleRate(22050, [44100, 44100]);
            Expect(low == 22050, $"a stored 22,05 kHz came back as {low}");
        });

        failures += Check("upgrading adopts what the servers already say", () =>
        {
            // The failure this is here to prevent: somebody who deliberately set 48 kHz on every
            // server opens the version where the rate became one setting, and is moved to 44,1
            // without being told, because nothing on screen changes until they next go live.
            var everything48 = EncoderSettings.ResolveSampleRate(null, [48000, 48000, 48000]);
            Expect(everything48 == 48000, $"a station running entirely at 48 kHz was moved to {everything48}");

            var everything22 = EncoderSettings.ResolveSampleRate(null, [22050, 22050]);
            Expect(everything22 == 22050, $"a station running entirely at 22,05 kHz was moved to {everything22}");
        });

        failures += Check("a mixed list takes the rate most of it was already on", () =>
        {
            var majority = EncoderSettings.ResolveSampleRate(null, [44100, 48000, 48000, 48000]);
            Expect(majority == 48000, $"three servers at 48 kHz and one at 44,1 resolved to {majority}");

            // A tie goes to the higher rate: resampling down loses nothing that was not going to be
            // lost anyway, and resampling up cannot put back what was thrown away.
            var tie = EncoderSettings.ResolveSampleRate(null, [44100, 48000]);
            Expect(tie == 48000, $"an even split resolved to {tie} rather than the higher rate");
        });

        failures += Check("a Deck with no servers still has a rate", () =>
        {
            var fresh = EncoderSettings.ResolveSampleRate(null, []);
            Expect(fresh == EncoderSettings.DefaultSampleRate,
                $"a first run with no servers resolved to {fresh}");

            Expect(EncoderSettings.DefaultSampleRate == 44100,
                "the default is not 44,1 kHz, which is what every host expects");
        });

        failures += Check("the offered rates are ones a codec can actually take", () =>
        {
            // The menu under Sound is one list for every codec, so each entry has to be usable by at
            // least one of them - an option nothing can use is a trap.
            foreach (var rate in EncoderSettings.OfferedSampleRates)
            {
                var usable = Enum.GetValues<StreamCodec>()
                    .Any(codec => EncoderSettings.AvailableSampleRates(codec).Contains(rate));

                Expect(usable, $"{rate} Hz is offered under Sound and no codec accepts it");
            }

            Expect(EncoderSettings.OfferedSampleRates.Contains(EncoderSettings.DefaultSampleRate),
                "the default rate is not one of the rates offered");
        });

        failures += Check("a codec that cannot take the chosen rate moves to one it can", () =>
        {
            // Opus is the case that matters: it accepts 16, 24 and 48 kHz and nothing else, so the
            // setting is a preference rather than a promise. Deck normalises rather than refusing,
            // and the server editor says so on the row where the number differs.
            foreach (var rate in EncoderSettings.OfferedSampleRates)
            {
                var opus = new EncoderSettings { Codec = StreamCodec.OggOpus, SampleRate = rate }.Normalised();

                Expect(EncoderSettings.AvailableSampleRates(StreamCodec.OggOpus).Contains(opus.SampleRate),
                    $"Opus was left at {opus.SampleRate} Hz, which it cannot encode");
            }

            var stays = new EncoderSettings { Codec = StreamCodec.Mp3, SampleRate = 48000 }.Normalised();
            Expect(stays.SampleRate == 48000, "MP3 at 48 kHz was moved, and it had no reason to be");
        });

        failures += Check("choosing a preset no longer chooses a sample rate", () =>
        {
            // Presets carry a rate only because the record needs one. Comparing the whole record
            // meant somebody running at 48 kHz saw every preset as "custom" and could not pick one,
            // and applying a preset would have dragged them back to 44,1.
            foreach (var preset in QualityPreset.All)
            {
                var at48 = preset.Settings with { SampleRate = 48000 };

                Expect(QualityPreset.Match(at48) == preset,
                    $"\"{preset.Name}\" is not recognised once the station runs at 48 kHz");
            }

            var different = QualityPreset.MusicStandard.Settings with { BitrateKbps = 160 };
            Expect(QualityPreset.Match(different) is null,
                "a bitrate nobody offers as a preset was matched to one anyway");
        });

        failures += Check("the setting survives being written and read back", () =>
        {
            var saved = RoundTrip(new AppSettings { SampleRate = 48000 });
            Expect(saved.SampleRate == 48000, $"48 kHz came back as {saved.SampleRate}");

            // Null is load-bearing rather than a missing value: it is what says "nobody has chosen",
            // and it has to survive the file for the upgrade above to work at all.
            Expect(new AppSettings().SampleRate is null,
                "a fresh AppSettings claims a sample rate has been chosen when none has");

            Expect(RoundTrip(new AppSettings()).SampleRate is null,
                "an unchosen rate came back from the file as a chosen one");
        });

        return failures;
    }

    private static AppSettings RoundTrip(AppSettings settings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"deck-rate-{Guid.NewGuid():N}.json");

        try
        {
            var store = new SettingsStore(path);
            store.Save(settings);
            return store.Load();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }
}
