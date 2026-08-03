# Photographing the deck

The palette section of the website is fourteen screenshots: every palette, drawn light and drawn
dark. They are photographs of the product rather than drawings of it, which is the whole reason they
are worth showing — and also the reason they can go stale, because nothing about changing a colour in
`Palettes.cs` changes a picture in `site/shots/`.

`capture.ps1` takes them again. It is the only way to take them: the check
**"the pictures on the website are of the palettes they are captioned with"** opens every one, counts
its pixels, and fails if the background is not exactly that palette's background or if the meter is
not lit in that palette's own green.

```powershell
pwsh tools/shots/capture.ps1
dotnet run --project tests/Deck.EncoderCheck
```

It takes about fifteen minutes, almost all of it waiting.

## The one step it cannot do for you

**Before running it, make the app format numbers in English.** Deck follows Windows for that, so on a
Norwegian machine the readouts come out as `-9,8 dB` and `1,5 MB sent` — correct there, and wrong on
an English page. Three lines at the top of `App.OnStartup`, removed again afterwards:

```csharp
CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-GB");
Thread.CurrentThread.CurrentCulture = new CultureInfo("en-GB");
```

It is a temporary edit and not a setting, deliberately: the product should keep following Windows.
Somebody who picked English words did not thereby ask for English decimal points, and a machine in
Oslo showing `-9,8 dB` is right. It is only the pictures on an English page that need it.

If you skip this step nothing breaks and no check fails — the numbers will simply be in your own
format. Check the pictures before pushing them.

## What it starts

Nothing here touches the Deck you broadcast with. `%APPDATA%\Deck` is never opened.

- **A copy of the app** in `%TEMP%\deck-shots`, with a `deck-portable.txt` beside it so it keeps its
  settings in its own folder.
- **`tools/fakecast`** — an Icecast imitated closely enough to broadcast to, on `127.0.0.1`. This is
  what makes the clock run, the bytes count up and the listener count say `12`. None of those numbers
  is drawn on: they are the product reporting a real connection to a real socket.
- **`player/`** — a stand-in backing-track player, built as `Karaoke Player.exe` so that the input
  chip on the deck reads the name of something a venue would recognise. It plays into
  **Steam Streaming Speakers**, a render endpoint that goes nowhere, so the room stays quiet.
- **`bed.py`** — writes the twenty seconds of shaped noise it plays. Fixed seed, peaks at −8 dBFS,
  channels a shade apart so the two meter rows are not a mirror of each other. This is what puts the
  meter in the green and the coaching line on *Sounds good*.

Every picture is a fresh start rather than a palette switch on a running window. Switching would give
the same picture in all but one respect: if a control ever held on to a brush from the palette before
it, only a fresh start would show it.

## What you need

- Windows, PowerShell 7, the .NET 8 SDK, and Python for `bed.py`.
- **A render endpoint that goes nowhere.** The script looks for one called *Steam Streaming Speakers*
  because that is what was on the machine these were taken on. Any silent virtual device will do —
  pass its name in `$silent` — but if you point this at real speakers, it will play twenty seconds of
  noise into the room on a loop for a quarter of an hour.

## Two things to know before relying on it

**These projects are not in `Deck.sln`.** Every push to `main` cuts a release, and a harness that runs
by hand does not belong on the path between a commit and a download. The cost is that they can stop
compiling without anybody finding out until the next time somebody needs them.

**The names in the pictures are chosen, not found.** The station is *Karaoke Night*, the programme is
*Open mic - stage 1*, and both are set by `capture.ps1`. Everything else on screen — the level, the
coaching, the bitrate, the bytes, the listeners — is the product's own reading of what was actually
happening.
