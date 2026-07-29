using Sirs.Core;
using Sirs.Core.Audio;
using Sirs.Core.Codecs;

namespace Sirs.EncoderCheck;

/// <summary>
/// Checks the hot-plug watchdog (A6). A source is stopped out from under the engine, which is what
/// a driver reset or an unplugged interface looks like from the inside, and the watchdog should
/// take it back on its own.
/// <para>
/// This exercises the recovery path, not Windows' device-removal notification: nothing here
/// physically unplugs anything. Real unplug behaviour still wants a hand test.
/// </para>
/// </summary>
internal static class RecoveryCheck
{
    public static int Run()
    {
        Console.WriteLine("--- Hot-plug recovery ---");

        var input = AudioDevices.Inputs().FirstOrDefault(d => d.IsSystemDefault)
            ?? AudioDevices.Inputs().FirstOrDefault();

        if (input is null)
        {
            Console.WriteLine("FAIL: no input device to test with\n");
            return 1;
        }

        Console.WriteLine($"  using: {input.Name}");

        using var engine = new BroadcastEngine();
        var recovered = string.Empty;
        engine.DeviceRecovered += (_, e) => recovered = e.Message;

        try
        {
            engine.StartAudio(input.Id, AudioDeviceKind.Input, QualityPreset.Default.Settings);

            if (!engine.Capture.Primary.IsRunning)
            {
                Console.WriteLine("FAIL: capture did not start\n");
                return 1;
            }

            Console.WriteLine("  capture running; simulating the device dropping out");
            engine.Capture.Primary.Stop();

            if (!engine.IsWaitingForDevice)
            {
                Console.WriteLine("FAIL: engine does not report that it is waiting for the device\n");
                return 1;
            }

            // The watchdog ticks every two seconds.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && !engine.Capture.Primary.IsRunning)
            {
                Thread.Sleep(250);
            }

            if (!engine.Capture.Primary.IsRunning)
            {
                Console.WriteLine("FAIL: the watchdog did not bring the input back within 8 seconds\n");
                return 1;
            }

            if (string.IsNullOrEmpty(recovered))
            {
                Console.WriteLine("FAIL: capture restarted but no recovery message was raised\n");
                return 1;
            }

            if (engine.IsWaitingForDevice)
            {
                Console.WriteLine("FAIL: still reporting a wait after recovery\n");
                return 1;
            }

            Console.WriteLine($"  recovered: \"{recovered}\"");
            Console.WriteLine("PASS\n");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }
    }
}
