using Sirs.Core.Audio;

namespace Sirs.EncoderCheck;

/// <summary>
/// Pure logic checks for the ring buffer behind the second mixer source. Wrap-around and
/// overflow are exactly the cases that work in testing and fail an hour into a show.
/// </summary>
internal static class RingBufferChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Case("round trip", () =>
        {
            var ring = new FloatRingBuffer(16);
            ring.Write([1, 2, 3, 4]);

            if (ring.Count != 4) return $"count was {ring.Count}, expected 4";

            Span<float> read = stackalloc float[4];
            var got = ring.Read(read);

            if (got != 4) return $"read returned {got}, expected 4";
            if (!read.SequenceEqual([1f, 2f, 3f, 4f])) return "samples came back in the wrong order";
            if (ring.Count != 0) return $"count was {ring.Count} after draining, expected 0";
            return null;
        });

        failures += Case("wraps around the end", () =>
        {
            var ring = new FloatRingBuffer(8);
            ring.Write([1, 2, 3, 4, 5, 6]);

            Span<float> first = stackalloc float[4];
            ring.Read(first);

            // Writing now straddles the end of the array.
            ring.Write([7, 8, 9, 10]);
            if (ring.Count != 6) return $"count was {ring.Count}, expected 6";

            Span<float> rest = stackalloc float[6];
            ring.Read(rest);

            return rest.SequenceEqual([5f, 6f, 7f, 8f, 9f, 10f])
                ? null
                : $"got [{string.Join(", ", rest.ToArray())}], expected [5, 6, 7, 8, 9, 10]";
        });

        failures += Case("overflow drops the oldest", () =>
        {
            var ring = new FloatRingBuffer(4);
            ring.Write([1, 2, 3, 4]);
            ring.Write([5, 6]);

            if (ring.Count != 4) return $"count was {ring.Count}, expected 4";
            if (ring.DroppedSamples != 2) return $"dropped {ring.DroppedSamples}, expected 2";

            Span<float> read = stackalloc float[4];
            ring.Read(read);

            return read.SequenceEqual([3f, 4f, 5f, 6f])
                ? null
                : $"got [{string.Join(", ", read.ToArray())}], expected the newest four [3, 4, 5, 6]";
        });

        failures += Case("a write larger than the ring keeps its tail", () =>
        {
            var ring = new FloatRingBuffer(4);
            ring.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

            if (ring.Count != 4) return $"count was {ring.Count}, expected 4";

            Span<float> read = stackalloc float[4];
            ring.Read(read);

            return read.SequenceEqual([7f, 8f, 9f, 10f])
                ? null
                : $"got [{string.Join(", ", read.ToArray())}], expected [7, 8, 9, 10]";
        });

        failures += Case("under-run pads with silence", () =>
        {
            var ring = new FloatRingBuffer(16);
            ring.Write([1, 2]);

            Span<float> read = stackalloc float[5];
            read.Fill(99f);
            var got = ring.Read(read);

            if (got != 2) return $"read returned {got}, expected 2 real samples";

            return read.SequenceEqual([1f, 2f, 0f, 0f, 0f])
                ? null
                : $"got [{string.Join(", ", read.ToArray())}], expected the tail zeroed";
        });

        failures += Case("skip discards the oldest", () =>
        {
            var ring = new FloatRingBuffer(16);
            ring.Write([1, 2, 3, 4, 5, 6]);
            ring.Skip(2);

            if (ring.Count != 4) return $"count was {ring.Count}, expected 4";

            Span<float> read = stackalloc float[4];
            ring.Read(read);

            return read.SequenceEqual([3f, 4f, 5f, 6f])
                ? null
                : $"got [{string.Join(", ", read.ToArray())}], expected [3, 4, 5, 6]";
        });

        failures += Case("skipping more than held is safe", () =>
        {
            var ring = new FloatRingBuffer(16);
            ring.Write([1, 2]);
            ring.Skip(100);
            return ring.Count == 0 ? null : $"count was {ring.Count}, expected 0";
        });

        return failures;
    }

    private static int Case(string name, Func<string?> verify)
    {
        var problem = verify();
        if (problem is null)
        {
            Console.WriteLine($"  ok    {name}");
            return 0;
        }

        Console.WriteLine($"  FAIL  {name}: {problem}");
        return 1;
    }
}
