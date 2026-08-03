# A programme bed to photograph the meter against.
#
# The deck's meter is one of the things the palettes actually colour, so a grid of screenshots taken
# against silence would show none of it. This is not music - it is a steady, slightly moving band of
# filtered noise sitting where a well-set show sits: peaks near -8 dBFS, a couple of dB between the
# two channels so the rows are not a mirror of each other, and correlated between them so the deck
# reads the phase as in-phase rather than as a fault.

import math
import random
import struct
import wave

RATE = 44100
SECONDS = 20
PEAK_DBFS = -8.0
random.seed(4711)


def pinkish(n):
    """Voss-McCartney, three rows. Enough spectral tilt to sound and meter like a programme bed."""
    rows = [0.0, 0.0, 0.0]
    counters = [0, 0, 0]
    periods = [2, 8, 32]
    out = []
    white = 0.0
    for i in range(n):
        for r in range(3):
            counters[r] += 1
            if counters[r] >= periods[r]:
                counters[r] = 0
                rows[r] = random.uniform(-1, 1)
        white = random.uniform(-1, 1)
        out.append((sum(rows) + white) / 4.0)
    return out


n = RATE * SECONDS
base = pinkish(n)

# A slow swell, so the meter is alive between frames rather than a frozen bar.
shaped = []
for i, v in enumerate(base):
    t = i / RATE
    env = 0.80 + 0.20 * math.sin(2 * math.pi * t / 6.5) * math.sin(2 * math.pi * t / 2.3)
    shaped.append(v * env)

peak = max(abs(v) for v in shaped)
gain = (10 ** (PEAK_DBFS / 20.0)) / peak

frames = bytearray()
for v in shaped:
    left = v * gain
    right = v * gain * 0.84  # ~1.5 dB down, and the same signal, so phase reads +1
    frames += struct.pack("<hh", int(left * 32767), int(right * 32767))

with wave.open("bed.wav", "wb") as f:
    f.setnchannels(2)
    f.setsampwidth(2)
    f.setframerate(RATE)
    f.writeframes(bytes(frames))

print(f"bed.wav  {SECONDS}s  peak {20 * math.log10(peak * gain):.1f} dBFS")
