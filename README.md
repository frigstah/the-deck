# The Deck

**A live radio broadcaster for Windows, built as one screen.**

Icecast and SHOUTcast, everything a small station needs, and a window you can read from across the
room. Free and unrestricted: no paid tier, no bitrate cap, no locked features. [GPL-3.0](LICENSE).

> **Beta.** The Deck has streamed live to a real Icecast server and its encoders are verified in
> detail, but it has never been run by anyone other than its author. Expect rough edges, and read
> [Not yet verified](#not-yet-verified) before trusting it with a show that matters.

## The idea

The Deck holds around twenty feature areas, and shows you five things.

Everything you need while a show is going out — whether you are on air, for how long, what you sound
like, where it is going, and what listeners are seeing — is on one dark screen with no tabs and
nothing to scroll. **The meter is the interface.** Everything that *configures* anything lives behind
a single Setup button, as a panel that covers the deck and says so.

That split is the whole design. A setting cannot be reached by accident at 3 a.m., because reaching
one is a deliberate act. And the deck never has to compete for space with a settings screen, so it
can be sized to be read rather than sized to fit.

The window is 1020×482 on purpose. It is an instrument panel, not a document: it parks along the
edge of a screen and leaves the rest to whatever is playing the music.

## Lineage

The Deck is a fork of [SIRS](https://github.com/frigstah/SIRS), which is the same audio engine behind
a different idea about interfaces — a navigation rail with one subject per pane. SIRS answers "how do
you fit twenty feature areas into one window?" by making them all reachable. The Deck answers it by
deciding that during a show you need five of them, and putting the other fifteen behind a door.

Both are GPL-3.0 and both keep the full history of the other. The engine — encoders, meters, source
protocols, the verification suite — is shared ancestry and improvements in either are worth carrying
across.

See [Deck-Feature-Spec.md](Deck-Feature-Spec.md) for the full feature set and where it came from.

---

## Build and run

Requires the .NET 8 SDK.

Commands below are relative to this folder (the one holding `Deck.sln`). If your terminal opens a
level up, prefix the paths with `Deck/` or `cd Deck` first.

```bash
dotnet build Deck.sln
```

```bash
dotnet run --project src/Deck.App/Deck.App.csproj
```

Verification harness — encodes a known tone and checks every codec's output is real, measures the
loudness meter against the published EBU compliance cases, checks the spectrum against a brute-force
DFT, drives both local endpoints over real sockets, opens every ASIO and MIDI device on the machine,
and runs the parser and recording checks:

```bash
dotnet run --project tests/Deck.EncoderCheck/Deck.EncoderCheck.csproj
```

Deck also answers a command line, which it sends to the copy already running rather than opening a
second window. The remote control has to be switched on first, under "Deck itself":

```bash
Deck.exe --status
```

To build an installer and a portable zip (needs [Inno Setup](https://jrsoftware.org/isdl.php) for
the installer; the zip is built either way):

```bash
pwsh ./build/package.ps1
```

That produces three things in `publish/`: a per-user installer, a portable zip carrying the
`deck-portable.txt` marker, and the update package the built-in updater downloads. `SHA256SUMS.txt`
holds the digests, which the updater checks rather than trusts.

**Every push to `main` publishes an alpha.** `.github/workflows/alpha.yml` runs the check suite, and
if it passes, builds and publishes a pre-release tagged `v1.0.0.<run number>` — so the built-in
updater always has something newer to find. Worth knowing before pushing: a commit becomes a
download within a few minutes, and the tag pins it permanently. It is also why anything you would not
want published needs to be out of the source *before* the push rather than in a follow-up commit.

---

## Layout

```
src/Deck.Core/          No UI. Everything below is usable headless.
  Audio/                WASAPI and ASIO capture, metering + level coaching, channel selection,
                        resampling, format conversion, monitoring, sound check, automatic on-air
  Audio/Dsp/            Voice Enhance, AGC, 3-band EQ, multiband compressor, safety limiter,
                        BS.1770 loudness meter, spectrum analyser, stereo phase meter
  Codecs/               MP3 (LAME), Ogg Opus (Concentus), Ogg Vorbis, Ogg FLAC, Ogg muxer, presets
  Control/              Remote control endpoint, command-line parsing, MIDI mapping and input
  Net/                  The small HTTP server both local endpoints are built on
  Servers/              Server profiles, DPAPI-encrypted passwords, paste-a-URL parser, type probe
  Streaming/            Icecast and SHOUTcast source clients, buffering, reconnect state machine,
                        multi-destination broadcast set
  Diagnostics/          Staged connection tester with plain-language errors, session log
  Metadata/             Manual, text-file, Windows media session and local HTTP endpoint sources
  Localisation/         String catalogue, English reference, community translation packs
  Recording/            Recorder with filename templates, encoded, lossless or WAV
  Updates/              Opt-in release check. Never downloads or installs anything.
  BroadcastEngine.cs    Ties it together; owns the single audio callback

src/Deck.App/           The deck, the setup panel, server editor, first-run wizard, tray, hotkeys
  MainWindow.xaml       The deck in row 1, setup and mini mode over the top of it, the strip below
  SettingRow.cs         One setting: label left, control right, hairline between. 54 of these.
  Theme.xaml            Both palettes and every style. Dark is applied over it in App.xaml.cs.
  LevelMeterControl.cs  The segmented meter, drawn; SpectrumControl.cs the 24-band spectrum
tests/Deck.EncoderCheck/ Encoders, DSP, parsers, endpoints, MIDI, ASIO, palettes and contrast
```

Config lives in `%APPDATA%\Deck` — its own folder, not SIRS's, so the two can be installed side by
side without overwriting each other's servers. Dropping a file named `deck-portable.txt` next to the
executable switches to portable mode, with config in a `data` folder alongside it.

---

## Design rules

These decided every argument during the build. The first two are The Deck's own; the rest are
inherited from SIRS and still right.

1. **The deck holds no settings.** If it configures something it lives behind Setup. What is on
   screen during a show is only what you need during a show.
2. **The deck reports; setup configures.** Nothing states the same fact in both places. The status
   strip appears only while setup covers the deck, so exactly one of the two is always answering
   "am I still live?".
3. **Plain language over jargon.** "Stream address", not "mount point".
4. **Every failure explains itself.** Never a raw socket error — `HTTP 401` becomes
   *"The server rejected the username or password for 'My Station'."*
5. **Test before you trust.** Inputs and destinations both have a test that answers in seconds.
6. **Safe defaults, visible escape hatch.** 128 kbps MP3 works everywhere; Advanced holds the rest.
7. **Free and unhobbled.** No feature gating, ever.

One rule was dropped on the way from SIRS: *"everything is one click deep, at most."* The Deck cannot
honour it — a setting is now two clicks away, Setup then the pane. That was the trade, and it was
worth making: the alternative was a first screen that looks like a control panel.

---

## What is built (Phases 1–4 / P0–P3)

| Area | Done |
|---|---|
| Audio in | WASAPI capture, input gain, anti-aliased resampling |
| Input sources | Microphones and line inputs, plus WASAPI loopback — broadcast the sound this PC is playing, from any app. ASIO interfaces appear too, where a driver is installed |
| Input channels | Pick which inputs on a multi-channel interface feed the stream, or one side of a stereo input |
| Mixing | Two sources with independent faders, mutes and meters — music under a live microphone |
| Hot-plug | An input that drops out is taken back automatically the moment it returns |
| Level coaching | Stereo peak meter with zone colouring, peak-hold marker, traffic-light verdict |
| Loudness | BS.1770 / EBU R128 metering in LUFS, momentary and whole-show, against a chosen target |
| Frequencies and phase | An optional panel, closed by default: a 24-band spectrum, and a stereo phase reading that catches a miswired cable or an over-widened source before mono listeners lose it |
| Sound check | Record 10 s and play it straight back, with a verdict on the level |
| Monitoring | Headphone output on a separately chosen device, feedback warning |
| Dead air | Silence alarm while live |
| Servers | Named profiles, add/edit/duplicate/delete, DPAPI-encrypted passwords |
| Setup | Paste-a-URL and paste-an-email parsing, automatic server-type detection, host presets |
| Sharing | Export the server list to a file another DJ can import; passwords deliberately stay behind |
| Protocols | Icecast HTTP PUT with automatic SOURCE fallback, SHOUTcast v1 and v2, TLS |
| Several servers at once | A main plus a backup relay, or the same show at two bitrates; one dropping does not take the others off air |
| Testing | Six-stage connection test: find, connect, secure, identify, sign in, send audio |
| Encoding | MP3, Ogg Opus, Ogg Vorbis and lossless Ogg FLAC, 32–320 kbps, quality presets plus full manual control |
| Processing | Voice Enhance, automatic level control, bass/middle/treble, a preset-driven three-band compressor, always-on safety limiter |
| Metadata | Manual now-playing, polled text file, the Windows media session, and a local endpoint your playout software can post to — including the Icecast admin form, unchanged |
| Title format | `{artist} - {title}` templates with a live preview, and a hold switch for adverts and jingles |
| Recording | While broadcasting or standalone, in the stream's format, lossless FLAC or WAV; filename templates, auto-split by duration, stops itself before the disk fills |
| Shell | Notification-area icon coloured by on-air state, global hotkeys, auto-connect on start, automatic on-air when sound appears |
| Remote control | An opt-in local endpoint other programs can drive Deck from, and the same commands on the command line — `Deck --live`, `--status`, `--title "…"` — which reach the copy already running |
| MIDI | Physical buttons and faders from a control surface, mixer or keyboard, assigned by pressing Learn and moving the control |
| Accessibility | Standard controls throughout; the drawn meters publish their level as text for screen readers |
| Reliability | Send buffer, 1 s reconnect backoff, clear connection state machine, live throughput and buffer statistics |
| Session log | Connects, drops, device trouble and track changes, shown in-app and appended to a daily file |
| Listeners | Live count from Icecast, SHOUTcast v1 and v2 where the server reports it, summed across destinations |
| Language | English built in, community translations as JSON files with coverage shown and English as the fallback |
| Updates | Opt-in check against the GitHub releases, and a one-click install: Deck downloads the new build, checks it against the digest published beside it, closes, replaces itself and starts again. Refused while on air |
| Installing | A per-user installer that needs no administrator rights, and a portable zip that keeps its settings beside the executable. Every push to `main` publishes both as an alpha pre-release |
| UI | Single window laid out as a navigation rail — one subject in the pane at a time, and an on-air strip along the bottom that is present on every pane. First-run wizard, High-DPI, follows Windows light/dark or stays on the one you pick |

### Verified

- **Encoders.** A 440 Hz tone survives MP3 and Ogg Opus round trips. The Opus stream is parsed back
  by an independent demuxer whose CRC is checked against the catalogued Ogg test vector
  (`0x89A1897F`), confirming page framing, BOS/EOS flags, sequence numbers, `OpusHead`/`OpusTags`,
  and a decode returning exactly 3 s of 440 Hz audio at the input amplitude.
- **Paste-a-URL parser.** Twelve cases covering listen URLs, embedded credentials, bare host:port,
  control-panel emails, SHOUTcast stream ids, implied TLS, and inputs that must be rejected —
  including the same block spelled out with Windows and with bare-carriage-return line endings,
  after CI caught the parser failing on CRLF while passing locally.
- **Loopback capture.** A tone played through a render device is captured back off that same device
  and arrives at the expected level. Run it with `-- --loopback`; it is opt-in because it needs
  audio hardware and briefly makes real sound.
- **Mixing.** The ring buffer behind the second fader is covered by unit checks for wrap-around,
  overflow, under-run and drift skipping. A live check (`-- --mixer`) puts the same playing device
  on both faders and confirms the mix rises well above a single source.
- **Hot-plug recovery.** A source is stopped out from under the engine — what a driver reset looks
  like from the inside — and the watchdog takes it back and reports it (`-- --recovery`).
- **Media session.** The Windows API opens and the poll loop runs (`-- --metadata`). Whether a
  title appears depends on something playing, so on a quiet machine this check is deliberately
  inconclusive rather than green.
- **Listener counts.** Nine parsing cases across the shapes each server family returns, including
  Icecast's single-object-vs-array switch and SHOUTcast's HTML-wrapped stats line.
- **Session log.** Verified live: a real connect and disconnect appeared in the window and in the
  day's file, in the same words the UI used.
- **Sharing server settings.** Round-trip fidelity, passwords never reaching the exported file, and
  importing your own export adding servers rather than replacing them.
- **Ogg Vorbis.** Page structure, the three Vorbis headers, channel count and sample rate, and a
  final granule position accounting for every sample fed in.
- **Ogg FLAC.** The strictest check in the suite, because FLAC is lossless and so the answer can be
  exact rather than approximate: an independently written decoder verifies both frame checksums and
  every decoded sample must equal the input **bit for bit**. Run against a tone, and again against
  digital silence, white noise, hard-panned content and a full-scale square wave, so the constant,
  verbatim and fixed-predictor paths and the stereo decorrelation are all exercised.
- **Loudness.** Six compliance cases from EBU Tech 3341, including both gates, checked to the ±0.1 LU
  the standard requires. The numbers come from the standard rather than from this code, which is the
  point: a K-weighting filter with a wrong coefficient still produces a plausible-looking figure.
  Also verified at 44.1 kHz, where the coefficients are derived rather than quoted.
- **Sound processing.** The three compressor bands sum back flat to within 0.00 dB from 60 Hz to
  12 kHz, including at both crossover frequencies — the property that lets it be left on without
  colouring audio that needs no work. Plus: one band compressing does not duck the others, the tone
  controls move only their own range, and "Off" is a true bypass.
- **Several servers at once.** Format conversion from one capture stream to destinations at
  different rates and channel counts, ending in a check that a converted destination's MP3 frames
  declare the sample rate and channel count its server was actually told.
- **Recording.** Lossless and WAV recordings follow the captured audio rather than the broadcast's
  settings, verified by decoding the files back and reading their declared rate and channel count.
- **The now-playing endpoint.** Twenty cases over a real socket: the Icecast admin form, form posts,
  URL escaping, the password, and a check that binding to loopback really does refuse a connection
  from this machine's own network address.
- **Title templates.** Missing pieces take their punctuation with them, so a station ident never
  goes out as " - Station Ident".
- **Input channels.** Choosing input 3 on a four-input device really takes input 3, and a single
  input into a mono stream is not quietly halved by being averaged with silence.
- **Automatic on-air.** It waits out a brief noise before going live, and twenty half-minute gaps
  between tracks do not take a station off air.
- **Translations.** A partly translated pack is used where it has text and falls back to English
  everywhere else; coverage is reported honestly; a corrupt language file is skipped rather than
  fatal.
- **The update check.** It refuses to open anything that is not an ordinary http(s) page, so a
  release feed cannot point Deck at a local executable.
- **The spectrum.** Checked against a brute-force DFT written separately in the test — a different
  algorithm, not a rearrangement of the same one — agreeing to within 1e-9 on a signal built from
  three unrelated tones and a DC offset. Plus: a tone lands in the bar whose label covers it, a
  full-scale tone reads at the top, and the bars fall smoothly rather than flicker.
- **Stereo phase.** Identical channels read +1 and lose nothing in mono; an inverted channel reads
  −1, is reported as a fault in words, and is shown as cancelling almost completely; two unrelated
  tones read near zero and are called wide rather than broken.
- **MIDI.** Every path below the driver, driven from raw bytes: message decoding including the
  zero-velocity note-off that most controllers actually send, learning, saving and reloading, a
  damaged settings line losing only the damaged part, and — the one that matters — a held button
  firing once rather than thirty times. A real MIDI interface on the build machine opens, stops and
  reopens cleanly.
- **ASIO.** Three of the four drivers on the build machine open, and a Behringer X-AIR interface
  delivered 48128 samples in 500 ms at 48 kHz — exactly the expected rate. This found a real bug:
  ASIO drivers are COM objects needing a single-threaded apartment, so the first version worked when
  clicked (WPF's UI thread is STA) and would have failed every time the device watchdog tried to
  recover an interface, because a timer callback is not.
- **Remote control and the command line.** Twelve cases over a real socket, plus a live end-to-end
  run: with Deck running, `Deck.exe --status` from a separate process reported the real meter and
  loudness readings, `--title` set a title with accents and an em dash intact, `--mute` and `--gain`
  took effect, and a command Deck could not honour returned a non-zero exit code. Killing Deck
  outright leaves the handshake file behind, and the next command correctly reports "not running"
  rather than hanging on a dead port.
- **The installer and the updater, for real.** The installer was built, installed silently, and the
  result checked: per-user location, correct version stamp, uninstall entry present, no portable
  marker. Then a genuinely newer build was staged and handed the running copy's process id, and the
  install went from 1.3.0.9001 to 1.3.0.9002 with the new version left running — those numbers
  predate the renumber to 1.0.0 and are left as the run actually happened; the actual
  download-close-replace-restart sequence, minus the download. Repeated against a portable layout,
  where the marker and the `data` folder both survived untouched. Uninstalling left `%APPDATA%\Deck`
  alone, as intended.
- **Every way the updater says no.** Nine checks covering the host allow-list (lookalike domains,
  plain http, `file://`, an unlisted GitHub host), a release with no checksum, a release pointing
  off GitHub, a payload with no `Deck.exe`, and the command line that triggers a file copy over an
  install directory — which must never be reachable by accident.
- **Both themes, on real hardware.** The window was captured from the running app in the Windows
  light and dark settings, across several panes, with a live signal on the meter. This is how the
  meter's peak-hold crash was found: it only fired once the level rose above the floor, so it looked
  intermittent and theme-related when it was neither.
- **The rail layout.** Every one of the seven panes was rendered from the running app and checked
  by eye, and all seven rail entries are reported to UI Automation as selectable tab items, so the
  navigation is reachable by keyboard and by a screen reader rather than only by mouse. Two defects
  were found this way and fixed: the on-air button took its colour from the connection state, which
  made the single most important control in the program look disabled while off air, and the quality
  summary appeared twice on screen at once on the Servers pane.
- **Screen-reader output.** The drawn meters report live values through UI Automation — confirmed
  against the running app, which read back "Loudest -27 decibels".
- **Every control on a pane is reachable by a screen reader.** Walking the automation tree of the
  running app found that it was not: the rail reported seven tabs and *nothing inside any of them*,
  so every picker, slider, checkbox and button on all seven panes was invisible to assistive
  technology. `TabControl` publishes the selected pane's contents only through the content host it
  finds by the name `PART_SelectedContentHost`, and the rail template had left it unnamed. Naming it
  took the window from five reachable buttons to every one of them.
- **Both palettes define the same colours.** Checked as text, because nothing else catches it: the
  light palette lives in `Theme.xaml` and the dark one is applied over it in code, so a colour added
  to one and forgotten in the other builds, runs, and leaves a single stubbornly light element on a
  dark window. All 29 keys are present in both.
- **Contrast, computed rather than judged.** The WCAG relative-luminance formula over both palettes:
  text and hint text to AA, the on-air block and every meter zone to the large-element bar. It caught
  two colours on the first run — light hint text at 4.3:1 against the new ground, and a quiet meter
  zone lifted so far from "slab" that lit and unlit were 1.44:1 apart. Both colours were fixed rather
  than the thresholds lowered.
- **The deck and every setup pane render.** Each of the seven panes selected in turn through the
  automation tree and photographed, which is also how they were confirmed to expose themselves to a
  screen reader — Sound publishes 68 automation elements, Control 60.
- **The app itself.** Runs, captures live audio from a real interface, the level coaching responds
  correctly, and selecting a loopback source switches the on-screen guidance to match. Both palettes
  were looked at on the deck and in setup, which is where three faults were found that no test would
  have reported: the meter's spare height splitting above *and* below it, an empty outlined chip where
  the quality goes before a server exists, and a black wordmark block sitting in the corner of the
  light theme with no rail underneath it.
- **A live broadcast.** Deck has connected to a real Icecast server and streamed MP3 at 256 kbps,
  48 kHz stereo over a plain connection, and it sounded right on the listening end. The source
  handshake, encoder and send path are proven end to end.

### Not yet verified

One live Icecast broadcast is proven (above). These paths still have not met a real server:

- **SHOUTcast v1 and v2.** Written to spec, including the automatic port fallback, but never run
  against a DNAS. The fallback in particular deserves a real test, since it depends on how a given
  host quotes its ports.
- **TLS.** The secure path has only been exercised against the plain one's code, never a real
  certificate.
- **Reconnection under real network loss**, and long-run stability across a full show.
- **A physically unplugged device.** The recovery path is tested, but nothing in the harness pulls
  a cable, so Windows' own removal notifications have not been exercised.
- **Mixer drift over a long session.** Two devices on separate clocks are corrected a frame at a
  time; that has not yet been watched across hours.
- **A real media-session title.** The API is proven reachable, but no title has been read from an
  actual player. Play something and run `-- --metadata` to confirm.
- **A listener count with someone actually listening.** The whole loop is now watched end to end
  against a loopback server built to behave like a real shared host, and the fallback chain has been
  run against a real one read-only. What is left is a real audience: nothing has yet reported a figure
  above nought that came from a stranger's media player.
- **Global hotkeys and the tray icon.** Both are constructed without complaint and the app runs
  normally, but nothing here presses Ctrl+Shift+G or clicks the notification area. Worth five
  minutes by hand.
- **Auto-split and the low-disk stop.** The logic is in place but has not been watched roll a file
  at the hour mark, nor met a genuinely full disk.
- **Vorbis, Opus and FLAC against a live server.** Only MP3 has completed a real broadcast. All
  three round-trip correctly offline, but no server has been asked to accept an Ogg stream, and some
  hosts are fussy about the content type. FLAC is worth checking earliest — it is roughly six times
  the bandwidth, and plenty of hosts cap that.
- **Two servers at once against two real servers.** The fan-out, the format conversion and the
  aggregate state are all covered offline, but no second server has been connected in anger, and the
  behaviour that matters — staying on air while one destination fails — needs a real failure.
- **No external player has opened a Deck FLAC file.** The bit-exact round trip is proven against an
  independently written decoder, which is a strong result, but VLC and foobar2000 have not been
  asked for a second opinion. Neither is installed here.
- **The processing presets have not been listened to.** Talk, Music and Loud are measured to behave
  correctly; whether they *sound* right is a judgement no test makes. Expect to adjust them.
- **The loudness figure has not been cross-checked against another meter.** It matches the EBU
  compliance cases, which is the meaningful bar, but a second opinion from a known-good meter on
  real programme material would be worth having.
- **Only part of the app is routed through the translation catalogue.** The framework is complete
  and proven — packs, fallback, coverage, template export — but the strings that currently go
  through it are the level coaching, the connection states, the server-setup problems and the
  listener count. Everything else, including all the XAML labels, is still literal English. A
  translator today would get a partly translated app, which is why coverage is shown rather than
  claimed.
- **The update check cannot see anything while this repository is private.** GitHub answers 404 to
  an unauthenticated caller, exactly as it would for a repository that does not exist, so Deck
  reports that it cannot see the release list and stops there. The whole feature — checking,
  downloading, verifying — only begins working when the repository is public. Nothing has yet been
  downloaded from a real release; the swap was tested with a locally staged build instead.
- **Nothing is code-signed.** Windows SmartScreen will warn about the installer and the executable,
  and there is no signature for anyone to verify an update against. This is the single biggest gap
  in the update story and no amount of checksumming closes it.
- **Automatic on-air has not run a real unattended show.** The decision logic is well covered, but
  nothing here has left it running overnight against a live server.
- **A broadcast from an ASIO input, end to end.** The capture layer is proven against real hardware,
  but no show has gone out from an ASIO interface, and nothing has unplugged one mid-broadcast to
  see whether the watchdog takes it back. ASIO drivers also allow only one program at a time, so
  what happens when a DAW is already holding the interface has not been watched from the UI.
- **A real MIDI controller has not pressed a button.** Every path below the driver is checked from
  raw bytes and a real interface opens, but no physical fader has been moved. What a specific desk
  actually sends — particularly whether its buttons latch or send momentary values — is the kind of
  thing only hardware settles.
- **The spectrum and phase panel has not been looked at while live.** The numbers behind it are
  verified; whether 24 bars at that decay rate read well in motion is a judgement no test makes.
- **The rail layout has not been used to run a show.** Every pane has been rendered and checked, and
  the rail is reachable by keyboard and reported correctly to screen readers — but nobody has yet
  spent two hours broadcasting with it and found out which pane they wish they were on.
- **The remote control has not been driven by real automation software.** The endpoint is proven
  from a socket and from Deck's own command line, but no playout system has been pointed at it.
- **The title bar does not offer Windows 11's Snap Layouts.** Hovering the maximise button on a
  system title bar shows the layout flyout; reproducing that means answering hit-test messages with
  `HTMAXBUTTON` and driving the hover and click states by hand. Dragging to a screen edge and the
  <kbd>Win</kbd>+arrow shortcuts are unaffected — only the hover flyout is missing.
- **Message boxes are still the system's.** The "you are still on air" confirmation and the update
  and crash notices use `MessageBox`, which cannot be themed. They will look like Windows, not like
  Deck.
- **Nobody has watched the deck during a real show.** The whole claim of the layout is that five
  readouts at that size answer everything you need while on air. That is a claim about a person
  halfway through a sentence glancing up from four metres away, and it has been tested by looking at
  screenshots. It may turn out the clock wants to be bigger, or that the chips are noise, or that a
  sixth thing is missing.
- **The on-air state has only been seen off air.** Every screenshot of both palettes shows OFF AIR.
  The lamp, the red state block and the elapsed clock are all bound and the colours are checked for
  contrast, but no capture exists of the deck while it is actually live.
- **The light theme has not been used for a show.** It is designed and its contrast is computed, but
  the dark one is what has been lived in.
- **The first-run wizard has not been re-fitted to the deck.** It is inherited from SIRS unchanged and
  still describes a window that no longer exists in quite that shape.

---

## Notable decisions

- **The deck holds no settings at all, and that is the fork.** Everything configurable is behind one
  flag. It costs a click and buys a first screen that cannot be misread, cannot be fiddled with by
  accident mid-show, and never has to share space with a settings form.
- **The window is short on purpose.** 482 pixels. The deck has five rows and then it is finished, so a
  taller window would only add emptiness under it. An earlier version was 760 and the slack pooled
  above and below the meter, which made three things look adrift in a dark rectangle instead of one
  instrument.
- **The status strip appears only while setup is open.** The deck states the same facts in far larger
  type, so showing both would say everything twice on one screen. Exactly one of the two is always
  visible, so the answer to "am I live?" never leaves.
- **"No listener count" is a different answer from "nought listeners".** Both used to look like an
  empty space, and a station owner staring at one could not tell whether nobody had tuned in or the
  server never tells Deck. Now the number is the number, not knowing says so, and the reason is on
  hover and in the log. Getting the number at all takes three tries — Icecast's JSON stats, the older
  plain-text table, then the mount's own admin stats using the broadcast password — because a shared
  host can remove the public status pages and leave a healthy server that publishes nothing.
- **Three sizes, and the middle one is the strip.** Press Mini and Deck becomes a 56-pixel bar that
  stays on top of other windows: the on-air block, the meter, the destination, record, go live, and the
  way back. Nothing else, and no route to a setting — for that you need the deck, which is one press or
  one double-click away. The deck is for when Deck is what you are doing; the notification area is for
  when it is out of mind; the strip is for the case in between, which is most of a show. It is
  remembered between runs, because parking it along the top of a screen is a way of working rather than
  a passing choice.
- **Setup has no header row.** It held the word SETUP, which the rail already says, and a close button,
  which belongs in the strip with the other things you press. Removing it freed exactly the height the
  rail needed to show all seven entries in a window this short — the difference between a rail you
  scroll and one you just read.
- **Every setting is a row, through one template.** 54 of them. The panes inherited from SIRS put a
  label above each control in one column, which is how a *form* is built — and a form reads as
  something to fill in top to bottom whether or not you care about any of it. A list of rows reads as
  something to scan.
- **Shape follows what a thing is, not what it resembles.** Bass, middle and treble became three rows;
  the mixer's two faders stayed a grid. They look alike, but tone is three independent settings with no
  second axis, whereas a mixer source has a level *and* a mute. Same reasoning keeps the backup
  tick-list and the destination status as lists: their length depends on how many servers exist.
- **Dark is the default, and light is designed rather than inverted.** A lit meter on a dark ground is
  how broadcast equipment has answered "am I on air?" for fifty years. The light palette had to be
  rethought, not recoloured: on a pale ground a lit segment is *darker* than the ground, so carrying
  the dark theme's quiet grey drew a heavy slab across two thirds of the meter and made a quiet signal
  look loud.
- **The settings folder is The Deck's own.** It is a fork, so both are installable side by side, and
  sharing one file would have each overwrite the other's servers whenever it closed.
- **The DPAPI entropy still says SIRS.** It is not a secret, only a value that has to match whatever
  encrypted a password — so a `servers.json` copied across from SIRS still decrypts. Renaming it would
  have silently blanked every carried-over password.
- **No AAC.** The patent pool costs money to redistribute. Opus is free and better below 96 kbps.
  MP3 patents expired in 2017, so LAME ships freely.
- **Opus via Concentus**, a managed port, so there is no native Opus DLL to ship per architecture.
- **Recording runs its own encoder** rather than tapping the broadcast's. It costs a little CPU and
  buys the ability to record with no server configured at all.
- **Authentication failures stop retrying.** A wrong password never comes right on its own, and
  hammering the server buries the real reason under reconnect messages. Everything else retries.
- **SHOUTcast port fallback.** SHOUTcast takes broadcasts on the port after the listener port, and
  hosts are split on which they quote. Deck tries both and says which worked.
- **Ogg pages never split a packet.** Opus packets are far below the 65025 bytes that would force a
  continuation, so the muxer skips that bookkeeping entirely and stays spec-compliant.
- **FLAC is implemented here rather than bound to libFLAC**, for the same reason Opus uses Concentus:
  a native codec means shipping and loading a DLL per architecture. It uses fixed polynomial
  predictors rather than full LPC — a few percent of compression given up for a fraction of the CPU,
  which is the right trade for a live encoder that may be running alongside three others.
- **A live broadcast can be Live while a destination is failing.** A backup exists precisely so one
  server going down does not take the show off air; reporting "Reconnecting" over a perfectly good
  main stream would be a lie. What went wrong is named in the status line instead.
- **The updater installs, but the repository is pinned and the download is verified.** Deck spent
  four phases deliberately *not* installing its own updates, on the grounds that an encoder able to
  replace its own binary can be made to run someone else's code by whoever controls the URL. That
  changed on request, so the argument had to be answered rather than dropped: there is no setting
  for where updates come from, only `github.com` over https is fetched, and a download whose
  SHA-256 does not match the digest published beside it is deleted unread. What that does **not**
  do is protect against a compromised GitHub account — the file and its digest come from the same
  place. Closing that needs a signing key that never touches CI, and these builds are not signed at
  all. Anyone who finds that unacceptable can leave the check off and install by hand, which still
  works and is still what the button did before.
- **Updates are refused while on air.** Taking a station off the air to install an update is not
  something Deck should decide to do, and an update that waits ten minutes costs nothing.
- **The installer is per-user, not Program Files.** That is what lets the updater replace the files
  without a UAC prompt. An updater that needs elevation is one that gets cancelled.
- **An update never carries the portable marker across.** That one file decides whether settings
  live beside the executable or in `%APPDATA%`, so copying it — or copying its absence — would
  silently move somebody's servers and settings somewhere they never put them. The install keeps
  whichever it already had.
- **The now-playing endpoint is loopback-only unless you say otherwise, and opening it up requires a
  password.** It is a listening socket on someone's machine; it should be as small a target as it
  can be, and off entirely until asked for.
- **Recording lossless follows the captured audio, not the broadcast's settings.** Recording a mono
  64 kbps show as a mono FLAC would preserve nothing worth preserving.
- **No VST hosting.** The VST3 SDK is GPLv3 or a commercial agreement, but the licence is the
  smaller half. Hosting means scanning plugins, opening someone else's editor window inside Deck,
  and surviving a plugin that crashes — a whole out-of-process subsystem whose failure mode is "the
  show went off air", built to serve users who by definition already own a DAW.
- **Remote control is separate from the now-playing endpoint, and off by default.** One of them
  changes what listeners read; the other can put a station on air. Someone who wants the first
  should not silently get the second. Both refuse outright to open to the network without a
  password rather than warning about it.
- **The command line talks to the running copy, and only ever over loopback.** Letting `Deck.exe`
  aim at a host given on the command line would turn it into a small tool for putting other
  people's stations off air.
- **MIDI buttons act on the press and not again until released.** Plenty of desks repeat their value
  while a button is held, which would otherwise toggle a station on and off many times a second.
- **The spectrum is closed by default.** It is the most encoder-shaped thing in Deck, and the whole
  argument for the program is that the first screen does not look like one. The phase reading beside
  it is the half that earns its place: nothing else in Deck can see a miswired cable.
- **ASIO drivers get a thread of their own.** They are COM objects requiring a single-threaded
  apartment, and the device watchdog runs on a timer, which is not one. Without a dedicated STA
  thread an ASIO input would open when chosen and then never recover from a glitch.
- **The window is a rail, not a wall of cards.** Twenty feature areas given equal weight in two
  scrolling columns is a control panel, whichever way it is styled. One subject per pane means the
  screen only ever holds what you came for — and the on-air strip along the bottom is the reason
  this layout won over the alternatives: the answer to "am I still live?" can no longer scroll off.
- **The rail is a `TabControl` underneath.** That control already means "one of these at a time", so
  it brings keyboard navigation, focus handling and screen-reader semantics with it. A stack of
  buttons driving a visibility flag would have looked identical and quietly lost all three.
- **The window draws its own title bar.** Windows will not let an application colour the system one,
  so a dark Deck sat under a light caption — a seam across the top of the product, whichever theme
  you were in. The caption is now two halves: a block of rail colour exactly the width of the rail,
  so the rail appears to run to the top of the window with the wordmark at its head, and beyond it
  the window's own background, so the rest of the caption simply *is* the pane. All that is left of
  a title bar is three buttons.
- **The dialogs draw their own title bar too.** One control, placed at the top of the wizard, the
  server editor and the log window, which works out from the window it is in which buttons make
  sense — the same rules the system caption follows — and applies the maximised-bounds correction so
  a window only has to place it to get the whole treatment.
- **Following Windows is a default, not a rule.** Appearance on the Deck pane offers Follow Windows,
  Light and Dark. Following the system is right for most people most of the time, which is why it is
  the default — but a studio PC is often left on the system light theme by whoever set it up, and
  the person sitting at it at midnight is not that person. Choosing outright makes Deck ignore the
  system entirely, including while it is running.
- **Switching the Windows theme repaints Deck immediately.** It used to need a restart, and the
  reason is worth recording because it looked like it worked. The palette is applied by overwriting
  colour keys, and Theme.xaml declares its brushes as `Color="{DynamicResource BackgroundColor}"` —
  but a brush living inside a resource dictionary resolves that reference *once*, when the
  dictionary realises it. Overwriting the colour afterwards leaves every realised brush on the old
  value. It only ever appeared to work because at startup nothing had realised the brushes yet. The
  palette is now applied by loading Theme.xaml afresh, writing the dark colours into that copy while
  nothing is using it, and swapping it in — which also means the light theme needs no code of its
  own and the two cannot drift apart.
- **A maximised window needs its edges giving back.** Windows positions a maximised window so its
  resize border falls outside the screen — invisible on an ordinary window, whose outer pixels are
  frame nobody draws in. With a custom caption those pixels are content, so the right of the close
  button and the bottom of the on-air strip went off the screen. The overhang is measured at each
  maximise, rather than assumed to be eight pixels, because it scales with the display.
- **The pane column is as wide as the window, not as wide as its text.** A left-aligned stack is
  only as wide as its widest child, and on the Sound pane that is usually the coaching sentence
  under the meter — which rewrites itself as you speak. The meter stretches to the column, so it was
  changing width by around 200 pixels every time the verdict changed: it visibly breathed with the
  audio it was measuring.
- **The level meter is segmented, not a solid bar.** A filled bar reads as a progress indicator,
  which is the wrong idea entirely — a level is not a thing that fills up. The unlit segments also
  keep the whole scale on screen, so the green target zone is visible while the level is somewhere
  else, which is what makes the meter teach rather than just report.
- **The accent is a petrol teal, not a UI blue.** It is the colour of instrument panelling, it does
  not compete with the on-air red the way a saturated blue does, and it leaves red meaning exactly
  one thing. Neutrals are warm-biased on light and slate-biased on dark; a pure grey reads as
  unchosen.
- **Labels are mono, small, uppercase and letter-spaced.** Legending on a control surface rather
  than captions on a form. WPF has no letter-spacing property, so the spacing is written into the
  strings — which is also why the labels are terse: every character costs twice.
- **Nothing is rounded except the verdict pill.** Square corners throughout mean the one rounded
  thing on screen reads as a badge rather than as another control.
- **The dark theme is designed, not inverted.** The accent has to come *up* in lightness to hold
  against a dark ground, which then makes white text on it unreadable — so there is a separate
  "text on accent" colour that flips to near-black. The pill fills become deep tints of their own
  hue rather than pale ones darkened, and the rail goes darker than the window rather than lighter,
  so it still reads as an edge instead of a raised panel.
- **The crash handler does not use a message box any more.** A modal box pumps the dispatcher, so a
  fault that recurs during layout raises again inside the box, which shows another box — forty
  levels deep until the stack overflows and the process dies silently. It now refuses re-entry,
  writes every exception to `logs/crash.log`, and tells the user once.

---

## Third-party components

| Component | Licence |
|---|---|
| NAudio | MIT |
| NAudio.Lame / LAME | LGPL (dynamically linked) |
| Concentus | BSD-3-Clause |
| OggVorbisEncoder | BSD-3-Clause |

MP3, Opus, Vorbis and FLAC are all free of patent-licensing obligations for distribution. FLAC is
implemented directly in Deck, so there is no third-party component for it.

---

## Licence

The Deck is free software under the **GNU General Public License, version 3 or later**. The full text
is in [LICENSE](LICENSE).

Copyleft rather than a permissive licence, and deliberately so. The last design rule is that The Deck
is free and unhobbled, with no feature gating ever — and the commercial encoder this exists to answer
paywalls multi-stream, SSL, AAC and fast reconnect. A permissive licence would let anyone take this
work, close it, and sell exactly the tiers The Deck was built to make unnecessary. The GPL is the only
part of that promise that survives contact with someone who disagrees with it.

All four dependencies above are compatible: MIT and BSD-3-Clause are permissive, and LGPL code that
is dynamically linked combines with GPL-3.0 without difficulty.

**Fork attribution.** The Deck is a fork of [SIRS](https://github.com/frigstah/SIRS), also GPL-3.0, and
carries its full commit history — so the lineage is in the repository itself rather than only in this
paragraph. The audio engine, the source protocols and most of the verification suite are shared
ancestry. What is original to The Deck is the interface: the deck window, the setting-row pane
language, and both palettes.
