<img src="branding/plate/icon-256.png" alt="" width="88" height="88">

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

**Every push to `main` publishes a beta.** `.github/workflows/alpha.yml` runs the check suite, and
if it passes, builds and publishes a pre-release tagged `v1.0.0.<run number>` — so the built-in
updater always has something newer to find. The file is still named `alpha.yml` on purpose:
`github.run_number` counts the runs of a workflow *path*, so renaming it would start the build
number again at 1 and publish a version every existing install reads as older than the one it
already has. Worth knowing before pushing: a commit becomes a
download within a few minutes, and the tag pins it permanently. It is also why anything you would not
want published needs to be out of the source *before* the push rather than in a follow-up commit.

---

## Layout

```
src/Deck.Core/          No UI. Everything below is usable headless.
  Audio/                WASAPI, ASIO and single-program capture, metering + level coaching, channel
                        selection, resampling, format conversion, monitoring, sound check, auto on-air
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
  Theme.xaml            Every style, and the Deck light palette as WPF's starting point
  LevelMeterControl.cs  The segmented meter, drawn; SpectrumControl.cs the 24-band spectrum
src/Deck.Core/Theming/
  Palettes.cs           Five palettes in two faces each, as data rather than as XAML
tests/Deck.EncoderCheck/ Encoders, DSP, parsers, endpoints, MIDI, ASIO, palettes and contrast

site/                   The website, one self-contained page, published to GitHub Pages on change
branding/               The mark as vector source, plus the two comparisons the design was picked from
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
| Level coaching | Stereo peak meter with a painted caution band at the top of the scale, peak-hold marker, traffic-light verdict. The amber shows from -7 dBFS; the coaching still calls a level hot at -4, as it always has |
| Loudness | BS.1770 / EBU R128 metering in LUFS, momentary and whole-show, against a chosen target |
| Frequencies and phase | An optional panel, closed by default: a 24-band spectrum, and a stereo phase reading that catches a miswired cable or an over-widened source before mono listeners lose it |
| Sound check | Record 10 s and play it straight back, with a verdict on the level |
| Monitoring | Headphone output on a separately chosen device, feedback warning |
| Dead air | Silence alarm while live |
| Servers | Named profiles, add/edit/duplicate/delete, DPAPI-encrypted passwords |
| Setup | Paste-a-URL and paste-an-email parsing, automatic server-type detection, host presets |
| Sharing | Export the server list to a file another DJ can import; passwords deliberately stay behind |
| Coming from BUTT | Point the same Import button at a BUTT config and its servers come across — addresses, ports, mounts and passwords, protected on the way in. Only the servers: devices, DSP and MIDI stay where they are |
| Protocols | Icecast HTTP PUT with automatic SOURCE fallback, SHOUTcast v1 and v2, TLS |
| Several servers at once | A main plus a backup relay, or the same show at two bitrates; one dropping does not take the others off air |
| Testing | Six-stage connection test: find, connect, secure, identify, sign in, send audio |
| Encoding | MP3, Ogg Opus, Ogg Vorbis and lossless Ogg FLAC, 32–320 kbps, quality presets plus full manual control |
| Processing | Voice Enhance, automatic level control, bass/middle/treble, a preset-driven three-band compressor, always-on safety limiter |
| Metadata | A title typed on the deck itself and remembered between runs, a polled text file, the Windows media session, and a local endpoint your playout software can post to — including the Icecast admin form, unchanged |
| Title format | `{artist} - {title}` templates with a live preview, and a hold switch for adverts and jingles |
| Recording | While broadcasting or standalone, in the stream's format, lossless FLAC or WAV; filename templates, auto-split by duration, stops itself before the disk fills |
| Shell | Notification-area icon coloured by on-air state, global hotkeys, auto-connect on start, automatic on-air when sound appears |
| Remote control | An opt-in local endpoint other programs can drive Deck from, and the same commands on the command line — `Deck --live`, `--status`, `--title "…"` — which reach the copy already running |
| MIDI | Physical buttons and faders from a control surface, mixer or keyboard, assigned by pressing Learn and moving the control |
| Accessibility | Standard controls throughout; the drawn meters publish their level as text for screen readers |
| Reliability | Send buffer, 1 s reconnect backoff, clear connection state machine, live throughput and buffer statistics |
| When it will not connect | The deck says which of the three it is — the password was refused, something else is already broadcasting, or the server is not answering — then whether Deck is still trying, then the full reason with nothing trimmed. The same for Icecast and both SHOUTcasts |
| Session log | Connects, drops, device trouble and track changes, shown in-app and appended to a daily file |
| Listeners | Live count from Icecast, SHOUTcast v1 and v2 where the server reports it, summed across destinations |
| Appearance | Five palettes — Deck, Rosé, Graphite, Arcade, Dragon — each drawn for light and for dark, chosen separately from light-or-dark and applied without a restart |
| Language | English built in, community translations as JSON files with coverage shown and English as the fallback |
| Updates | Opt-in check against the GitHub releases, and a one-click install: Deck downloads the new build, checks it against the digest published beside it, closes, replaces itself and starts again. Refused while on air |
| Installing | A per-user installer that needs no administrator rights, and a portable zip that keeps its settings beside the executable. Every push to `main` publishes both as a beta pre-release |
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
- **Every palette, on real hardware.** The window was captured from the running app in all five
  palettes and both brightnesses, across several panes, with a live signal on the meter. This is how the
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
- **The now-playing line, driven through the real UI.** Every state exercised on the running app by
  automation rather than reasoned about: the "Set" chip invoked, the box arriving focused in the same
  place and at the same height as the chip it replaced, a title typed and sent with Enter, the chip
  gone and the settings file carrying the title within the same second. Then Deck was closed and
  reopened and the title was still there. Pressing the title reopened the box with the title selected;
  Escape and moving the focus away both left the title alone and the file untouched. Switching titles
  to the Windows source locked the line — reported as disabled to automation, still legible — and left
  the remembered title alone; switching back unlocked it. This is also how a screen-reader defect was
  found: the line had an automation name, which *replaces* what gets read out for a button whose
  content is already text, so it announced "title listeners see" instead of the title. It carries help
  text instead now.
- **Every control on a pane is reachable by a screen reader.** Walking the automation tree of the
  running app found that it was not: the rail reported seven tabs and *nothing inside any of them*,
  so every picker, slider, checkbox and button on all seven panes was invisible to assistive
  technology. `TabControl` publishes the selected pane's contents only through the content host it
  finds by the name `PART_SelectedContentHost`, and the rail template had left it unnamed. Naming it
  took the window from five reachable buttons to every one of them.
- **Every palette defines every colour.** There are five palettes in two faces each, so the colours
  live in `Palettes.cs` as data the checks can simply ask, rather than in two files compared as text.
  `Theme.xaml` still declares the Deck light face — a resource dictionary has to be valid before any
  code runs — and is checked value by value against it, since that is the one copy left.
- **Contrast, computed rather than judged.** The WCAG relative-luminance formula over all ten faces:
  text, hint text and rail labels to AA; the on-air block, the status colours and every meter zone to
  the large-element bar. It has never once run clean first time. On the four new palettes it caught
  five colours, two of them in the palette that had already shipped — footer readouts at 4.4:1 on the
  status strip, and rail labels at 4.4:1 — both of which had been below AA since the light theme was
  drawn, because until now nothing measured text against those two grounds.
- **Neighbouring meter zones are told apart**, measured as perceptual distance in Lab rather than as
  contrast. Written as contrast first, and it was wrong in both directions: it failed Arcade's cyan
  running into its amber, which anybody can see is a boundary, and passed two browns of the same
  lightness, which nobody can.
- **Every brush the window is handed is re-read when the palette changes.** A `Brush` reaches the
  window through a binding, and a binding is only read again when the property says it changed —
  swapping the palette says nothing. So the deck repainted and the on-air button kept the colour of
  the palette before it. The check compares the brush properties that exist against the names the
  refresh raises, so adding one without refreshing it fails here.
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
- **A program that refuses to be captured.** Real programs work, including a karaoke player and a
  3D client. What has not been met is one running as administrator while Deck is not, which Windows
  refuses - the message for that case is written but has never been seen.
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
- **The on-air state has only been seen off air.** Every screenshot of every palette shows OFF AIR.
  The lamp, the red state block and the elapsed clock are all bound and the colours are checked for
  contrast, but no capture exists of the deck while it is actually live.
- **Only Deck dark has been used for a show.** The other nine faces are designed, measured and
  photographed from the running window, but the dark petrol one is what has been lived in. Rosé,
  Graphite, Arcade and Dragon have never had a broadcast run through them.
- **The palettes were checked against a number, not against an eye.** Contrast and perceptual
  distance are computed for every pair that matters, and both caught real faults — but no
  colour-blind person, and nobody in a bright room, has yet looked at Arcade or Dragon.
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
- **The now-playing line is the one readout that is also a control, and the title outlives the run.**
  Everything else on the deck reports and setup changes, which held right up against the one setting
  that changes every time you use Deck rather than once when you set it up: what the show is called.
  Making that the only reason to open setup mid-show was wrong. So the line is pressable — "Set" while
  there is nothing, because "nothing set" reported the hole without saying who could fill it, and the
  title itself once there is one, with chrome only under the pointer so at rest it still reads as the
  fact it is. Enter sends it, Esc and clicking away leave it alone: what that box sends goes straight
  out to listeners, so it takes a keypress and never a stray click. A typed title is then remembered
  between runs — it is a show name, and a weekly programme should not have to be renamed every week.
  Only typed ones: a line that came from a text file, from Windows or from an automation system is a
  fact about a track that has since finished, and bringing one back on the next launch would put a
  stale song in front of listeners. `--title` is not remembered either, for the same reason and one
  more — it is a few hundred titles an evening, and keeping them would rewrite the settings file on
  every track change. Under those sources the line locks — undimmed, because it is still
  what listeners are seeing — since committing a title there would not merely be overwritten by the
  next poll, it would switch the source to manual and quietly cut off the station's automation.
- **A half-known server type is still a usable one.** Deck's type picker used to offer Icecast,
  SHOUTcast v1, SHOUTcast v2 and "detect automatically", and that last one was really "I have no idea"
  — which meant a server nobody could fully classify could not be broadcast to at all. But the version
  is nearly never the thing in doubt. A host says "we're SHOUTcast" and stops there; a BUTT config
  records SHOUTcast without saying which; a banner has the word in it and no number beside it. All
  three were being flattened into "unknown", throwing away the one fact that actually decides how Deck
  talks to the server. So plain **SHOUTcast** is now a type in its own right. It connects: both
  versions share a single source handshake, and the only things the version decides — the `:#sid`
  suffix for a v2 stream other than the first, and the `sid` on metadata updates — are not needed to
  get on air. Then the server answers the question itself, in the reply to that handshake: `OK` from
  v1, `OK2` from v2. Better evidence than a banner on the listener port, and it costs nothing, because
  Deck was making the connection anyway. Narrowed one way only — `OK2` is a positive claim and is
  acted on, while a bare `OK` is merely the absence of one and leaves the profile saying SHOUTcast, as
  writing v1 onto a v2 server would silently drop the stream id from every metadata update afterwards.
  The payoff is largest exactly where it was needed: of fifty-four servers imported from a real BUTT
  config, fifty-one were SHOUTcast, and every one of them had previously been landing as undecided —
  each needing a successful probe of a port that, being a source port, has nothing to say to an HTTP
  request in the first place.
- **The switching cost is the server list, not the software.** Somebody with fifty stations saved in
  BUTT is not going to retype fifty addresses, ports and passwords to try something else, and no
  amount of design work on the rest of Deck answers that — so Deck reads their file. It is the same
  Import button, which works out which kind of file it was given rather than making anyone know the
  difference. Only the servers cross: a BUTT config also carries audio devices, DSP settings, window
  positions and MIDI bindings, and the device indices mean nothing outside BUTT while silently
  importing somebody's compressor into a different compressor would be worse than not importing it.
  A SHOUTcast entry arrives as SHOUTcast, with the version left open: BUTT does not record *which*
  SHOUTcast, and it does not have to, because both versions share one source handshake and the
  server states which it is in its reply. Passwords do come across, and get protected on the
  way in — BUTT keeps them in the clear, so for anyone importing, the copy Deck holds is the safer
  one. The real fifty-four server file this was built against is also what found the bug: two of the
  stations had names differing only in capitals, and matching sections case-insensitively quietly
  replaced an Icecast server with a copy of a SHOUTcast one.
- **One program, not the whole desktop.** The second source can be a single program - the backing-track
  player, and nothing else on the machine. That is what makes karaoke work: microphone on the main input,
  KaraFun on the second, two faders, one stream. Whole-desktop capture would take the notifications, the
  other window and Deck's own monitoring with it. Windows only offers this from build 20348, so below that
  Deck does not offer it at all rather than failing when somebody goes on air.
- **Programs you have open, not only the ones playing.** Windows lists a program's audio only while it is
  actually playing, so the first version of this could not offer a browser with a paused tab - the honest
  answer to "why can't I see Chrome?" was "Windows has nothing to hand over". The picker now has a second
  group for programs that are merely open, because setting up before pressing play is the normal order of
  things and capture works on a silent program.
- **A program is remembered by name, not by process id.** A pid is different every time the program
  starts. And because Windows only lists a program while it is playing, a chosen one is put back into the
  list when it goes quiet - otherwise refreshing during a pause would silently move the second source to
  something nobody picked, and the singer would find out on stage.
- **SHOUTcast is never sent a nameless broadcast.** A server that gets no station name accepts the
  password, answers OK, and closes the connection without a word - which reached the user as "the
  connection to the server was lost", four times over. Deck now always sends a name, falling back to the
  server's own label and saying so, because refusing to go live would stop a station that works. Icecast
  does not care, so it is not asked.
- **A broadcast that drops seconds after starting says what that means.** It is not the network; a server
  that accepts a sign-in and then hangs up is refusing the audio. The message now says how long it lasted,
  how much was sent, and what to check - and if the server explained itself on the way out, that sentence
  is quoted, because Deck listens to the socket for the whole broadcast now rather than only during the
  handshake.
- **"No listener count" is a different answer from "nought listeners".** Both used to look like an
  empty space, and a station owner staring at one could not tell whether nobody had tuned in or the
  server never tells Deck. Now the number is the number, not knowing says so, and the reason is on
  hover and in the log. Getting the number at all takes three tries — Icecast's JSON stats, the older
  plain-text table, then the mount's own admin stats using the broadcast password — because a shared
  host can remove the public status pages and leave a healthy server that publishes nothing.
- **Three sizes, and the middle one is the strip.** Press Mini and Deck becomes a 56-pixel bar that
  stays on top of other windows: the mark, the on-air block, the meter, the destination, record, go
  live, and the way back. Nothing else, and no route to a setting — for that you need the deck, which is one press or
  one double-click away. The deck is for when Deck is what you are doing; the notification area is for
  when it is out of mind; the strip is for the case in between, which is most of a show. It is
  remembered between runs, because parking it along the top of a screen is a way of working rather than
  a passing choice.
- **Setup slides, and "follow Windows" was not enough of an answer.** It arrives and leaves over 220ms,
  easing out on the way in and in on the way out, and the distance is the panel's own height so it is
  right at any window size. Deck skipped the movement entirely when Windows had animation effects turned
  off, which is the correct default and turned out to be the wrong *only* option: that setting gets
  switched off for an old machine's sake, by an IT policy, or by somebody who never knew it existed, and
  none of those people said anything about this slide. So it is three states like the palette — follow
  Windows, always, never — and when it is following, the hint says which way Windows currently has it,
  because otherwise there is no way to tell why setup does or does not move.
- **The strip's on-air sign carries the listener count: "ON AIR WITH 7 LISTENERS".** One sign for the
  two things you look up for during a show. The deck keeps them apart because it has a readout row to
  put the number in and saying it twice on one screen would be worse; the strip has no readouts at all,
  which is exactly why it goes in the sign there. It appears only when live and only when the number is
  actually known — "ON AIR WITH NO LISTENER COUNT" is a worse sign than "ON AIR". A hidden twin holding
  the longest form claims the width, because the meter takes whatever is left on that row and a block
  that resized as people tuned in and out would drag the meter sideways all show. The strip is not
  polled while off air: the count is not shown there, so asking would be traffic to somebody's server
  every fifteen seconds in exchange for nothing. It is a switch, under Deck itself, because the strip is
  the one part of Deck that ends up on a screen other people can see — a presenter who knows eleven are
  listening is informed, and a room that knows it is a different matter. Off, the sign reads "ON AIR"
  and the number is still on the deck.
- **The strip is the one place the mark is drawn large, and it holds two control heights.** It is the
  only part of Deck that floats over other programs with no title bar and no wordmark, so it is the
  only part that has to say whose strip it is — in the accent rather than the text colour, because the
  loudest thing on that row has to stay the on-air block and a 48-pixel near-white letter beside it
  would win that argument. The four things you can press came from three different styles and sized
  themselves to 31, 31, 32 and 32, which on a row this tight reads as a fault rather than a decision.
  Everything that reports or switches is one height now; going on air is the other, and being the only
  differently sized thing on the row is how it says it is a different kind of thing.
- **The installer had SIRS's identity, and that is not a cosmetic thing.** An `AppId` is the whole of a
  product's identity to Windows, and the fork inherited SIRS's verbatim — so The Deck was not a new
  program, it *was* SIRS. Inno looks that id up before showing the folder page, so it found SIRS's
  installation, ignored `DefaultDirName` and offered to install The Deck into a folder called SIRS.
  Accepting put `Deck.exe` and `SIRS.exe` side by side under one uninstaller, with SIRS's own files
  orphaned in the folder and its entry in Add or remove programs replaced. The rebrand scan could not
  have caught it twice over: it read only `src`, and the stray was not the word SIRS but a GUID. It
  reaches the installer and the packaging script now, and names that GUID as a forbidden value —
  because it is the one piece of the old product that copies across while reading as meaningless hex.
- **Minimising leaves Deck on the taskbar.** It used to hide the window and leave only the notification
  icon, which was the right answer before the strip existed and is the wrong one now: the strip does
  that job properly, and hiding is harder to undo than it looks, because Windows 11 puts new
  notification icons behind the overflow chevron — so a minimised Deck could be a hunt away with an
  empty taskbar in between. Still a switch for anyone who wants it, and the icon by the clock is there
  either way, so nothing was taken away.
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
- **The painted scale and the verdict are two different questions.** A scale is orientation: every
  meter on every desk has its top end painted amber and red, and it is painted *before* the level is
  a problem, because that is what tells you how much room is left. A verdict is a judgement about the
  show. Deck's bar carried one red segment and two amber ones out of sixty-four, which is not enough
  of a scale to read across a room; it is now two and three, and the coaching thresholds did not move
  to get there.
- **That distinction was learnt the hard way, in one release.** The first attempt widened the bands
  by moving the coaching with them — on the reasoning that a bar turning amber while the words say
  the level is good contradicts itself. It does not: what would contradict itself is the reverse. So
  the rule is one-way and checked at every twentieth of a decibel — the paint may run ahead of the
  words and may never lag behind them — and the three numbers that decide what Deck *says* are pinned
  by a check of their own, because they were once moved for the look of the thing and that is a
  reason to change the paint and never a reason to change the advice.
- **The zone counts are checked as segments, not decibels.** The scale is curved, so three decibels
  can be worth one segment near the bottom and five near the top. How much of the bar changed colour
  is the thing anybody actually cares about, so that is what the check measures.

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
- **Anything that fades has to be legible at its dimmest, not its brightest.** The credit line in
  Support pulses, and the first gold chosen for it measured a comfortable 4.6:1 against the light
  window — at full strength. At the bottom of its fade it was 2.4:1, so a line that passed the
  obvious test was unreadable for most of the time it was on screen. The contrast check measures it
  faded, the pulse is shallower than it was, and the two palettes carry genuinely different golds:
  the metallic shade everyone pictures is a pale yellow on white and only works on the dark window.
- **Authentication failures stop retrying.** A wrong password never comes right on its own, and
  hammering the server buries the real reason under reconnect messages. Everything else retries.
- **Paste-a-URL reads details that are lined up rather than labelled.** Control panels hand out
  tables, and a table loses every colon on the way into an email — so `Password     hunter2` was not
  a field, and Deck filled in everything except the password. Which reads as a policy: people assumed
  it would not carry passwords on purpose. Lines with no separator are now read too, but only when
  the label is one Deck already knows, because with a colon "whatever comes before it" is a fair
  guess at a label and without one there is nothing to stop a sentence becoming a field. Two spaces
  minimum, since one is prose and two is a column. The same pass fixed a worse case: a note after the
  value — `Password: hunter2 (case sensitive)` — used to be stored as part of the password *and*
  reported as understood, so the user was told it had worked and found out at Go live, with a server
  refusing a password that looked correct on screen. A label mentioning the **admin** password is
  still never taken: on Icecast that is a different secret that opens the control pages rather than a
  stream, and using it would fail to connect while putting a more valuable credential somewhere it
  was never meant to go.
- **The tray icon refuses updates after it is disposed**, and that guard is load-bearing rather than
  tidiness. Closing Deck while on air used to take the whole program down with "Object reference not
  set to an instance of an object" — from inside WinForms, on the way out, with nothing on screen to
  connect it to a tray icon. Stopping the broadcast changes the connection state one last time, that
  change reaches the UI through `BeginInvoke` so it is *queued*, and it therefore arrives after
  everything has been torn down. `NotifyIcon` answers that badly: its property setters do not throw
  `ObjectDisposedException` — `Dispose` nulls the hidden window it talks to Windows through, and the
  next assignment walks straight into it. Because the notification is queued rather than immediate,
  disposal order alone could not have fixed it, which is worth knowing before anyone decides the
  guard looks redundant. Reported by a user, reproduced by going on air to a server that will not
  answer and closing the window mid-reconnect. That reproduction is the regression test: it is a
  WPF/WinForms shutdown race, so the check suite cannot reach it.
- **A failure that cannot be read is not feedback.** The sinks already told the three failures apart —
  a refused password, a stream somebody else is on, a server that is not there — and each wrote a
  sentence naming the thing to go and change. Every word of it then arrived in a footer readout capped
  at 320 pixels with an ellipsis on the end, in the same grey as the byte counter: the diagnosis fit,
  the remedy did not, and there was nowhere to see the rest. Two things were wrong. The kind of
  failure was being dropped along with the exception, so the deck had a string it could not
  categorise and could say nothing *about* the problem, only quote it — the kind is now carried up
  beside the words. And the explanation had no room, so it has its own block in the deck's slack row,
  the one place where something appearing pushes nothing else around. Three lines in the order they
  are needed: the verdict, whether Deck is still trying or has stopped, then the detail. Somebody
  mid-show reads the first line and stops. Red is reserved for what waiting cannot fix, because
  colouring an ordinary reconnect the same as a dead password cries wolf every time a network hiccups.
- **SHOUTcast port fallback.** SHOUTcast takes broadcasts on the port after the listener port, and
  hosts are split on which they quote. Deck tries both and says which worked. When both fail it keeps
  whichever answer says more: the second port is a guess, and when the guess is wrong it fails with
  "nothing is listening" — which was then reported as the reason and buried a real reply from the
  port the user actually entered. Somebody whose stream was already taken got told their server could
  not be reached, and went looking at their connection instead of at the encoder still running upstairs.
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
- **Following Windows is a default, not a rule.** Light or dark on the Deck pane offers Follow
  Windows, Light and Dark. Following the system is right for most people most of the time, which is
  why it is the default — but a studio PC is often left on the system light theme by whoever set it
  up, and the person sitting at it at midnight is not that person. Choosing outright makes Deck
  ignore the system entirely, including while it is running.
- **Which colours and how bright are two questions.** Deck ships with five palettes — its own petrol
  teal, plus Rosé, Graphite, Arcade and Dragon — and every one of them is drawn twice, for light and
  for dark. They are two settings on screen for the same reason they are two in the file: picking a
  palette must never quietly overrule what somebody told Windows about their eyes or their room. A
  single list of ten combinations would have been a longer way of asking the same thing.
- **A palette is designed twice, not derived once.** Inverting a light palette gives a muddy accent
  and unreadable fills — an accent has to gain lightness to hold against a dark ground, and pill
  backgrounds have to become deep tints of their own hue rather than pale ones darkened. What *is*
  worked out rather than chosen is the last third of each palette: the soft fill behind a verdict and
  the unlit half of the meter are the same colour faded into the window, which carries no design
  decision and is how a twenty-first shade of nearly-the-background gets in by accident.
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
- **The icon is a table of hand-set pixel geometry, not one drawing scaled.** Five identity routes
  were drawn and compared at true size (see [branding/](branding/)); the chosen one is a D with its
  curve replaced by two 45-degree cuts and a counter that can hold a lamp. Below about 24 pixels a
  mark stops being a drawing and becomes a decision about which pixels are on, so each of the nine
  sizes has whole-pixel geometry and everything at 24 and under is drawn aliased on purpose — a
  45-degree edge on the pixel grid is a clean staircase, antialiased it is a soft one. The tray is
  16 pixels and that is where somebody checks whether their station is still on air, so it is the
  size that got the most attention rather than the least.
- **The application icon carries its own ground; the tray icon does not.** Windows gives an
  application no say in what colour the taskbar is, so a near-black mark disappears on a dark one and
  a near-white mark disappears on a light one — hence the petrol tile. The notification area is the
  one place the colour *is* known, so there the mark sits on it directly and takes the state colour
  whole. It does not light the lamp inside the counter: at 16 pixels the counter is six pixels across
  and a lamp in it is a detail nobody sees, whereas the whole letter turning from grey to red is
  caught out of the corner of an eye, which is the entire job of a tray icon.
- **The icon is checked, not trusted.** It is generated by tooling that is run by hand and then
  committed, which is the shape of thing that goes stale silently — and a missing `ApplicationIcon`
  costs nothing at build time and ships an application wearing the default sheet of paper. So three
  separate implementations read it: the generator writes the container, WPF's decoder reads it back,
  and the check suite parses it again and measures the letter against the geometry it is supposed to
  be. The first version of that check passed a deliberately broken icon, because a probe in the
  middle of the counter stays in the middle of the counter however far the letter moves; it measures
  bounding boxes now.

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
language, and the palettes.
