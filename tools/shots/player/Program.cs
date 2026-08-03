// Plays a bed on a loop into a render endpoint that goes nowhere, so the deck has a programme to
// meter and a program to name while the palette grid is photographed - and the room stays quiet.
//
// Arguments: <wav> <endpoint name fragment> <minutes>

using NAudio.CoreAudioApi;
using NAudio.Wave;

var wav = args.Length > 0 ? args[0] : "bed.wav";
var wanted = args.Length > 1 ? args[1] : "Steam Streaming Speakers";
var minutes = args.Length > 2 && int.TryParse(args[2], out var m) ? m : 30;

using var enumerator = new MMDeviceEnumerator();
var device = enumerator
    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
    .FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase));

if (device is null) return 1;

using var reader = new AudioFileReader(wav);
using var loop = new Loop(reader);
using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 120);

output.Init(loop);
output.Play();

Thread.Sleep(TimeSpan.FromMinutes(minutes));
output.Stop();
return 0;

/// <summary>NAudio has no looping stream of its own; this is the smallest one that works.</summary>
sealed class Loop(WaveStream inner) : WaveStream
{
    public override WaveFormat WaveFormat => inner.WaveFormat;

    public override long Length => long.MaxValue / 32;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var done = 0;
        while (done < count)
        {
            var read = inner.Read(buffer, offset + done, count - done);
            if (read == 0)
            {
                if (inner.Position == 0) break;
                inner.Position = 0;
                continue;
            }

            done += read;
        }

        return done;
    }
}
