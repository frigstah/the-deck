using Deck.Core.Audio;

namespace Deck.EncoderCheck;

/// <summary>
/// ASIO input (A8).
/// <para>
/// The last check opens every ASIO driver on the machine and waits for audio. That is the one that
/// found the real bug: ASIO drivers are COM objects requiring a single-threaded apartment, so the
/// first version opened fine when clicked - WPF's UI thread is STA - and would have failed every
/// time the device watchdog tried to recover an interface, because a timer callback is not.
/// </para>
/// <para>
/// It reports rather than fails when no driver can be opened, since a machine where every interface
/// is already in use is a normal state of the world and not a fault in Deck.
/// </para>
/// </summary>
internal static class AsioChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("a machine with no ASIO drivers still works", () =>
        {
            var drivers = AsioCapture.DriverNames();
            Console.WriteLine($"       ({drivers.Count} ASIO driver(s): {(drivers.Count == 0 ? "none" : string.Join(", ", drivers))})");

            // The important part: the device picker is built from this on every start, so throwing
            // here would stop Deck opening on any machine with a broken ASIO registry entry.
            var devices = AsioCapture.Devices();
            Expect(devices.Count == drivers.Count, "the device list and the driver list disagree");

            foreach (var device in devices)
            {
                Expect(device.Kind == AudioDeviceKind.Asio, $"\"{device.Name}\" was not listed as ASIO");
                Expect(!device.IsSystemDefault, "an ASIO device was offered as the system default");
            }
        });

        failures += Check("ASIO devices appear in the input list without displacing anything", () =>
        {
            var all = AudioDevices.AllInputSources();
            var asio = AsioCapture.Devices();

            foreach (var device in asio)
            {
                Expect(all.Any(d => d.Id == device.Id && d.Kind == AudioDeviceKind.Asio),
                    $"\"{device.Name}\" was missing from the input list");
            }

            // Microphones must still come first: ASIO is a minority case and must not push the
            // thing most people want down the list.
            var firstAsio = all.ToList().FindIndex(d => d.Kind == AudioDeviceKind.Asio);
            var lastOther = all.ToList().FindLastIndex(d => d.Kind != AudioDeviceKind.Asio);

            if (firstAsio >= 0)
            {
                Expect(firstAsio > lastOther, "ASIO devices were mixed in among the microphones");
            }
        });

        failures += Check("an ASIO id can never be mistaken for a Windows endpoint", () =>
        {
            var id = AsioCapture.IdFor("Focusrite USB ASIO");

            Expect(AsioCapture.IsAsioId(id), $"\"{id}\" was not recognised as an ASIO id");
            Expect(AsioCapture.DriverNameFrom(id) == "Focusrite USB ASIO",
                $"the driver name came back as \"{AsioCapture.DriverNameFrom(id)}\"");

            // A WASAPI endpoint id is a GUID-shaped string. It must not be taken for ASIO, or Deck
            // would try to open a driver by that name and report a device that does exist as missing.
            Expect(!AsioCapture.IsAsioId("{0.0.1.00000000}.{c1e0a2f3-0000-0000-0000-000000000000}"),
                "a WASAPI endpoint id was treated as ASIO");

            Expect(!AsioCapture.IsAsioId(null), "a missing id was treated as ASIO");
            Expect(!AsioCapture.IsAsioId(""), "an empty id was treated as ASIO");

            // Driver names can contain anything, including the prefix itself.
            var awkward = AsioCapture.IdFor("asio:weird name");
            Expect(AsioCapture.DriverNameFrom(awkward) == "asio:weird name",
                $"an awkward driver name round-tripped as \"{AsioCapture.DriverNameFrom(awkward)}\"");
        });

        failures += Check("a driver that will not open says so in plain language", () =>
        {
            using var capture = new AsioCapture("No Such Interface ASIO", 48000);

            try
            {
                capture.StartRecording();
                throw new Exception("opening a driver that does not exist reported success");
            }
            catch (AudioDeviceUnavailableException ex)
            {
                Expect(ex.Message.Contains("No Such Interface ASIO"),
                    $"the message did not name the driver: {ex.Message}");

                // A user has to be able to act on this. "in use by another program" is the actual
                // cause most of the time, since ASIO drivers allow only one client.
                Expect(ex.Message.Contains("another program", StringComparison.OrdinalIgnoreCase),
                    $"the message did not suggest what to try: {ex.Message}");
            }
        });

        failures += Check("stopping something that never started is harmless", () =>
        {
            // The device watchdog and the shutdown path both do this.
            var capture = new AsioCapture("No Such Interface ASIO", 48000);

            capture.StopRecording();
            capture.StopRecording();
            capture.Dispose();
            capture.Dispose();
        });

        failures += Check("a real ASIO driver opens and delivers audio", () =>
        {
            var drivers = AsioCapture.DriverNames();

            if (drivers.Count == 0)
            {
                Console.WriteLine("       (skipped: no ASIO driver on this machine)");
                return;
            }

            // Every driver on the machine, because they fail differently: one may be in use, one may
            // refuse the rate, one may not have hardware behind it at all. Any single success proves
            // the path; none succeeding is reported rather than failed, since a machine where every
            // interface is busy is a normal state of the world, not a bug in Deck.
            var opened = new List<string>();
            var delivered = new List<string>();
            var refused = new List<string>();

            foreach (var driver in drivers)
            {
                using var capture = new AsioCapture(driver, 48000);

                var blocks = 0;
                var samples = 0L;
                var format = "";

                capture.DataAvailable += (_, e) =>
                {
                    Interlocked.Increment(ref blocks);
                    Interlocked.Add(ref samples, e.BytesRecorded / sizeof(float));
                };

                try
                {
                    capture.StartRecording();
                    format = capture.WaveFormat.ToString();
                }
                catch (AudioDeviceUnavailableException ex)
                {
                    refused.Add($"{driver}: {Shorten(ex.Message)}");
                    continue;
                }

                opened.Add($"{driver} ({format})");

                // Half a second is plenty: an ASIO buffer is typically a few milliseconds.
                Thread.Sleep(500);
                capture.StopRecording();

                if (blocks == 0) continue;

                delivered.Add($"{driver}: {blocks} block(s), {samples} samples");

                Expect(capture.WaveFormat.Channels is 1 or 2,
                    $"{driver} reported {capture.WaveFormat.Channels} channels");

                Expect(samples % capture.WaveFormat.Channels == 0,
                    $"{driver} delivered {samples} samples, which is not a whole number of frames");
            }

            foreach (var line in opened) Console.WriteLine($"       opened   {line}");
            foreach (var line in delivered) Console.WriteLine($"       captured {line}");
            foreach (var line in refused) Console.WriteLine($"       refused  {line}");

            if (opened.Count == 0)
            {
                Console.WriteLine("       (no ASIO driver could be opened — all busy or without hardware)");
                return;
            }

            Expect(delivered.Count > 0,
                "an ASIO driver opened but never delivered a single block of audio");
        });

        return failures;
    }

    /// <summary>Driver messages are long; the useful part is the reason at the end.</summary>
    private static string Shorten(string message) =>
        message.Length <= 90 ? message : message[..90] + "…";

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
