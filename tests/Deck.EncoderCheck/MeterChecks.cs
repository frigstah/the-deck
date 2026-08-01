using Deck.Core.Audio;

namespace Deck.EncoderCheck;

/// <summary>
/// Where the meter changes colour (B1).
/// <para>
/// The boundaries used to be written out in three places - the drawn control, the live meter, and
/// the sound check - so this is partly a check that they are now written once. The rest of it is
/// about how much of the bar each zone actually occupies, which is not obvious from the numbers:
/// the scale is curved, so a decibel near the top is worth several near the bottom, and moving a
/// threshold by three decibels can be worth one segment or five.
/// </para>
/// </summary>
internal static class MeterChecks
{
    /// <summary>
    /// What the deck's own meter draws. <c>LevelMeterControl</c> asks for one segment per nine
    /// pixels and caps the count at 64; the window cannot be narrower than 820, which leaves the
    /// meter over 750 pixels, so the deck is always at the cap. The narrow meters beside the mixer
    /// faders are a different width and are meant to be coarser.
    /// </summary>
    private const int DeckSegments = 64;

    private const float FloorDb = -60f;

    public static int Run()
    {
        var failures = 0;

        failures += Check("the zones are in order and cover the whole scale", () =>
        {
            Expect(MeterZones.NoSignalDb < MeterZones.QuietDb, "no-signal has to be below the quiet threshold");
            Expect(MeterZones.QuietDb < MeterZones.LoudDb, "the quiet threshold has to be below the loud one");
            Expect(MeterZones.LoudDb < MeterZones.ClipDb, "the loud threshold has to be below the clip one");
            Expect(MeterZones.ClipDb < 0f, "the clip threshold has to be below full scale");

            Expect(MeterZones.QuietDb < MeterZones.BandLoudDb, "the amber paint starts below the usable range");
            Expect(MeterZones.BandLoudDb < MeterZones.BandClipDb, "the amber paint has to start below the red");
            Expect(MeterZones.BandClipDb < 0f, "the red paint has to start below full scale");

            // Nothing on the scale may fall outside a zone, which is what a gap in either switch
            // would look like: a segment drawn in the wrong colour rather than an error.
            for (var db = -90f; db <= 0f; db += 0.25f)
            {
                Expect(Enum.IsDefined(MeterZones.Zone(db)), $"{db:0.##} dB does not fall in any zone");
                Expect(Enum.IsDefined(MeterZones.Band(db)), $"{db:0.##} dB does not fall in any band");
            }
        });

        failures += Check("the paint runs ahead of the words, never behind them", () =>
        {
            // The rule that lets the two differ at all. A scale is orientation: painting amber before
            // a level is a problem is how a meter says "getting close", and every desk meter does it.
            // What must never happen is the reverse - the coaching calling a level hot while the bar
            // under it is still green, which would read as the software contradicting itself.
            //
            // The first version of this feature moved the verdict to match the paint, which fixed the
            // contradiction by warning everybody three decibels earlier than Deck ever had. This is
            // the same guarantee without that cost.
            for (var db = -90f; db <= 0f; db += 0.05f)
            {
                var words = MeterZones.Zone(db);
                var paint = MeterZones.Band(db);

                Expect(paint >= words,
                    $"at {db:0.##} dB the coaching says {words} while the bar is still painted {paint}");
            }

            // And it does run ahead somewhere, or the two would be the same thing under two names.
            var runsAhead = false;
            for (var db = -30f; db <= 0f; db += 0.05f)
            {
                if (MeterZones.Band(db) > MeterZones.Zone(db)) runsAhead = true;
            }

            Expect(runsAhead, "the paint never runs ahead of the words, so the caution band shows nothing early");
        });

        failures += Check("a level either side of a boundary lands on the right side of it", () =>
        {
            Expect(MeterZones.Zone(MeterZones.ClipDb) == MeterZone.Clip, "the clip threshold is not itself clipping");
            Expect(MeterZones.Zone(MeterZones.ClipDb - 0.1f) == MeterZone.Loud, "just under the clip threshold is not loud");
            Expect(MeterZones.Zone(MeterZones.LoudDb) == MeterZone.Loud, "the loud threshold is not itself loud");
            Expect(MeterZones.Zone(MeterZones.LoudDb - 0.1f) == MeterZone.Good, "just under the loud threshold is not good");
            Expect(MeterZones.Zone(MeterZones.QuietDb) == MeterZone.Good, "the quiet threshold is not yet good");
            Expect(MeterZones.Zone(MeterZones.QuietDb - 0.1f) == MeterZone.Quiet, "just under the quiet threshold is not quiet");
        });

        failures += Check("the coaching thresholds are the ones Deck has always used", () =>
        {
            // Pinned deliberately. These decide what Deck tells somebody about their show, and this
            // check exists because they were once moved to make the bar look better - which is a
            // reason to change the paint and never a reason to change the advice. Anybody with cause
            // to move them will have to delete this to do it, and that is the point.
            Expect(MeterZones.QuietDb == -24f, $"the quiet threshold is {MeterZones.QuietDb}, not -24");
            Expect(MeterZones.LoudDb == -4f, $"the loud threshold is {MeterZones.LoudDb}, not -4");
            Expect(MeterZones.ClipDb == -1f, $"the clip threshold is {MeterZones.ClipDb}, not -1");
        });

        failures += Check("the caution and clip zones are wide enough to be seen", () =>
        {
            // The reason the painted band exists at all. The bar carried one red segment and two
            // amber ones out of sixty-four, which is not enough of a scale to read across a room -
            // every other meter a broadcaster has used has a visible amber shoulder. One more of
            // each.
            //
            // Written as segment counts rather than as decibels because that is the thing anybody
            // actually cares about, and because the curve of the scale means the two are not the
            // same question: three decibels can be worth one segment or five depending on where they
            // are. Adjusting a threshold without meaning to move the colours on screen fails here.
            var counts = Count(DeckSegments);

            Expect(counts[MeterZone.Clip] == 2,
                $"the red part of the deck's meter is {counts[MeterZone.Clip]} segments, not 2");

            Expect(counts[MeterZone.Loud] == 3,
                $"the amber part of the deck's meter is {counts[MeterZone.Loud]} segments, not 3");

            // And the green aiming zone still has to be the larger part of the working end of the
            // bar, or the meter stops teaching where to sit and starts reading as a warning that is
            // always half on. Not most of the bar - most of the bar is below -24 dB and grey,
            // because the scale runs down to -60 - but comfortably more than the two zones above it
            // put together.
            Expect(counts[MeterZone.Good] > 2 * (counts[MeterZone.Loud] + counts[MeterZone.Clip]),
                $"the green aiming zone is {counts[MeterZone.Good]} segments against " +
                $"{counts[MeterZone.Loud] + counts[MeterZone.Clip]} of caution, which is not aiming at anything");
        });

        failures += Check("the narrow meters still show a caution zone at all", () =>
        {
            // The mixer faders draw a meter a fraction of the width, floored at ten segments. At
            // that size the old thresholds gave it no amber and no red whatsoever - the whole scale
            // was green until it clipped, which is worse than useless on the one control where
            // somebody is actively pushing a level up.
            var counts = Count(10);

            Expect(counts[MeterZone.Loud] + counts[MeterZone.Clip] >= 1,
                "the narrowest meter has no caution segment at all");
        });

        failures += Check("what Deck says about a level is what it has always said", () =>
        {
            // Driven through the real meter with real samples rather than by re-implementing the
            // call, so this is a check on the shipping behaviour and not on a copy of it.
            //
            // The levels below are chosen around the boundaries that moved and moved back. -5 dB is
            // the one that matters: it is inside the painted amber, and Deck still calls it good,
            // which is exactly the arrangement asked for.
            foreach (var (peak, expected) in new (float Peak, LevelAdvice Advice)[]
            {
                (-30f, LevelAdvice.TooQuiet),
                (-12f, LevelAdvice.Good),
                (-8f, LevelAdvice.Good),
                (-5f, LevelAdvice.Good),
                (-3f, LevelAdvice.Loud),
                (-2f, LevelAdvice.Loud),
            })
            {
                var advice = AdviceFor(peak);
                Expect(advice == expected, $"a {peak:0.#} dB peak is judged {advice}, not {expected}");

                // And the advice is the verdict zone, not the painted one. If these were ever wired
                // to Band the coaching would quietly start warning three decibels early again.
                var zone = MeterZones.Zone(peak);
                var agrees = (zone, advice) switch
                {
                    (MeterZone.Quiet, LevelAdvice.TooQuiet) => true,
                    (MeterZone.Good, LevelAdvice.Good) => true,
                    (MeterZone.Loud, LevelAdvice.Loud) => true,
                    (MeterZone.Clip, LevelAdvice.Clipping) => true,
                    _ => false,
                };

                Expect(agrees, $"at {peak:0.#} dB the coaching says {advice} but the verdict zone is {zone}");
            }
        });

        return failures;
    }

    /// <summary>
    /// How many segments of each colour a meter of this many segments draws, using the same maths
    /// the control does: the middle of a segment decides its colour, so it never lights up in a
    /// colour borrowed from the zone next door.
    /// </summary>
    private static Dictionary<MeterZone, int> Count(int segments)
    {
        var counts = Enum.GetValues<MeterZone>().ToDictionary(z => z, _ => 0);

        for (var i = 0; i < segments; i++)
        {
            var db = AudioMath.MeterScaleToDb((float)((i + 0.5) / segments), FloorDb);
            counts[MeterZones.Band(db)]++;
        }

        return counts;
    }

    /// <summary>Runs a tone at a known peak through the real meter and reads back its verdict.</summary>
    private static LevelAdvice AdviceFor(float peakDb)
    {
        var meter = new LevelMeter();
        var amplitude = AudioMath.FromDb(peakDb);

        var block = new float[4800];

        for (var i = 0; i < block.Length; i += 2)
        {
            var sample = amplitude * MathF.Sin(i * 0.05f);
            block[i] = sample;
            block[i + 1] = sample;
        }

        // A sine never lands exactly on its own peak in a finite block, so the first sample is
        // placed there outright: the level being judged has to be the level asked for.
        block[0] = amplitude;
        block[1] = amplitude;

        // Enough passes for the meter's rolling window to have settled on this level rather than on
        // the silence it starts from.
        for (var pass = 0; pass < 40; pass++) meter.Process(block, 2);

        return meter.Advice;
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
