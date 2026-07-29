# SIRS — Simple Internet Radio Streamer

**Windows live audio encoder for Icecast / SHOUTcast.**
Positioning: everything a small station actually needs, nothing it doesn't, and it explains itself.

---

## 1. Competitive baseline

### BUTT (Broadcast Using This Tool) — free, open source, cross-platform
| Area | What it has |
|---|---|
| Servers | Icecast, SHOUTcast, WebRTC (WHIP), SSL/TLS (Icecast only) |
| Codecs | MP3, Ogg Vorbis, Ogg Opus, Ogg FLAC; WAV recording |
| Input | Mic / line in, up to two multi-channel devices |
| Processing | 10-band EQ, dynamic range compressor |
| Metadata | Manual, text file, music-app scrape (macOS/Linux only) |
| Recording | Simultaneous with broadcast; auto start/stop on signal/silence |
| Connection | Auto-connect on startup, auto-reconnect, connect/disconnect on signal/silence |
| Control | CLI, network remote control, MIDI CC, 7 UI languages, UI scaling, listener count |

**Where it hurts users:** dense multi-tab settings dialog, jargon-first fields (mountpoint vs SID, "sample rate" before "does my mic work"), cryptic connection errors, no guided first-run, server config is a flat list with no test button.

### Rocket Broadcaster — freemium, Windows-only
| Area | What it has |
|---|---|
| Capture | App audio capture (Spotify, Skype…), mic/line-in, mixed together |
| Codecs | Free: MP3, Vorbis, Opus @128k. Pro: AAC, AAC+, HE-AACv1, Ogg FLAC, 16–320k |
| Servers | Icecast, Icecast-KH, SHOUTcast v1 & v2, RSAS, hosted services |
| Processing | Multiband compressor, AGC, limiter, ITU BS.1770 loudness metering; VST slot (Pro) |
| Metadata | Media-player scrape, manual, remote via TCP/UDP/text file, automation-system support |
| Recording | Auto-split hourly or by duration, auto-cleanup on low disk |
| UI | Stereo VU meters, High-DPI, keyboard control, screen-reader accessible, shareable project files |
| Reliability | Auto-reconnect, backup/redundant streams, built-in stream diagnostic test, logging |

**Where it hurts users:** the good stuff (multi-stream, SSL, AAC, fast reconnect, real VST) is behind a ~$100 paywall; free tier is deliberately hobbled (20s reconnect delay, 128k lock).

### The gap SIRS fills
Both are *encoder-shaped*: they assume you already know what a mountpoint is. Nobody has built the "it just works, and it tells you if it doesn't" version. SIRS's whole reason to exist is **first-run success in under 3 minutes** and **never wondering whether you sound OK**.

---

## 2. Design principles (these decide every feature argument)

1. **One window.** No settings maze. Main screen shows: input, level, big Connect button, status, elapsed time, listeners. Everything else is one click deep, max.
2. **Plain language over jargon.** "Stream address" not "mountpoint". Jargon is shown as a secondary label for people who need it, never as the primary one.
3. **Every failure explains itself.** Never surface a raw socket error. `HTTP 401` → *"The server rejected your password. Check the password for 'My Station'."*
4. **Test before you trust.** Every destination and every input has a Test button that gives a green/red answer in seconds.
5. **Safe defaults, visible escape hatch.** 128 kbps MP3 / 44.1 kHz stereo works everywhere. An "Advanced" disclosure holds the rest — nobody is forced through it, nobody is blocked by it.
6. **Free and unhobbled.** No feature paywall. That's the differentiator against Rocket.

---

## 3. Full feature set

Priority key: **P0** = v1.0 must-ship · **P1** = v1.1, strongly wanted · **P2** = later, valuable · **P3** = nice to have / evaluate

### 3.1 Audio input
| # | Feature | Pri | Notes |
|---|---|---|---|
| A1 | WASAPI input device capture (mic, line-in, USB interface) | P0 | Shared mode; exclusive as advanced option |
| A2 | Device picker with friendly names + live level preview *in the dropdown* | P0 | You see which device is receiving sound before you pick it |
| A3 | Input gain slider with numeric dB | P0 | |
| A4 | Desktop / application audio capture (WASAPI loopback) | P1 — **built** | Rocket's headline feature; big for music-from-Spotify stations. Pulled forward into Phase 1 on request |
| A5 | Mix two sources (e.g. mic + desktop) with individual faders | P1 | Deliberately capped at 2 — a full mixer breaks principle #1 |
| A6 | Device hot-plug handling: auto-recover if USB mic is unplugged/replugged | P1 | Common real-world failure; BUTT handles this poorly |
| A7 | Mono/stereo and channel-pair selection for multi-channel interfaces | P2 — **built** | |
| A8 | ASIO input | P3 — **built** | NAudio reaches ASIO through the COM interfaces drivers already expose, so no Steinberg SDK is redistributed. Verified against real hardware |

### 3.2 Sound test & monitoring — *the flagship area*
| # | Feature | Pri | Notes |
|---|---|---|---|
| B1 | Stereo peak + RMS meter, always visible on main screen | P0 | |
| B2 | **Traffic-light level coaching**: "Too quiet" / "Sounds good" / "Too loud — clipping" | P0 | Replaces expecting users to read dBFS |
| B3 | **Sound Check**: record 10s, play it back through chosen output | P0 | The single most useful thing neither competitor has. Answers "how do I actually sound?" |
| B4 | Headphone monitoring with selectable output device + feedback warning | P0 | Warn if monitor device could loop into the mic |
| B5 | Silence detection alert ("No audio for 15s") while live | P0 | Prevents dead air going unnoticed |
| B6 | **Test Connection**: connect, push a short test stream, report pass/fail per check (DNS → TCP → auth → mount → format accepted) | P0 | Rocket has a diagnostic; ours is a readable checklist |
| B7 | "Listen to my stream" — opens the public stream URL in the default player | P1 | End-to-end confirmation, one click |
| B8 | Loudness readout (ITU-R BS.1770 LUFS) with a target guide | P2 — **built** | Rocket has this; useful once users mature |
| B9 | Spectrum / correlation display | P3 — **built** | Collapsed by default. The correlation half turned out to be the valuable one: nothing else in SIRS can see a miswired cable |

### 3.3 Server / destination management — *second flagship area*
| # | Feature | Pri | Notes |
|---|---|---|---|
| C1 | Named server profiles, list view, add / edit / duplicate / delete | P0 | Duplicate matters: most people add a backup that differs by one field |
| C2 | **Paste-a-URL setup**: paste `http://host:8000/live` (or a host's config snippet) and the fields auto-fill | P0 | Biggest single reduction in setup friction |
| C3 | **Auto-detect server type** (Icecast vs SHOUTcast v1 vs v2) by probing the host | P0 | Removes the #1 confusing choice |
| C4 | Icecast source connect (HTTP PUT + legacy SOURCE) | P0 | |
| C5 | SHOUTcast v1 (ICY) and v2 support | P0 | v2 = P0 for compatibility, v1 for older hosts |
| C6 | TLS/SSL for Icecast (and SHOUTcast where supported) | P0 | Free, unlike Rocket |
| C7 | Per-server test button right in the edit dialog | P0 | See B6 |
| C8 | Public stream metadata fields: station name, description, genre, website, "list in directory" toggle | P0 | |
| C9 | Password show/hide + stored encrypted (Windows DPAPI) | P0 | Never plaintext in the config file |
| C10 | Import / export server profiles (single file) | P1 | Rocket's "shareable project files" — great for multi-DJ stations |
| C11 | Presets for common hosts (Radio Mast, RadioKing, Live365, Airtime, Shoutcast.com…) | P1 | Pick host → only asks for the fields that host actually needs |
| C12 | Simultaneous multi-server streaming (primary + backup, or different bitrates) | P2 — **built** | Rocket paywalls this; keep it out of v1 to protect simplicity |

### 3.4 Encoding
| # | Feature | Pri | Notes |
|---|---|---|---|
| D1 | MP3 (LAME) — patents expired, safe to ship | P0 | Default codec |
| D2 | Ogg Opus | P0 | Best quality per bit; modern |
| D3 | Ogg Vorbis | P1 | |
| D4 | Ogg FLAC | P2 — **built** | |
| D5 | Bitrate / sample rate / channels selection, full range 32–320 kbps | P0 | No artificial cap, ever |
| D6 | **Quality presets** ("Voice / Talk", "Music — Standard", "Music — High") mapping to sane combos, with raw controls behind Advanced | P0 | |
| D7 | AAC / HE-AAC | P3 — **declined** | **Licensing blocker**, and a permanent one. Royalties per unit, and FDK-AAC grants no patent rights. Paying breaks nothing but the budget; gating it breaks principle #6. Opus covers the quality need |
| D8 | Resampler with proper anti-aliasing | P0 | Needed whenever device rate ≠ stream rate |

### 3.5 Audio processing
| # | Feature | Pri | Notes |
|---|---|---|---|
| E1 | One-knob "Voice Enhance": HPF + gentle compression + limiter, off by default | P0 | Simple version of what BUTT/Rocket expose as many controls |
| E2 | Brickwall limiter always on the output path to prevent clipping the encoder | P0 | Invisible safety net |
| E3 | Automatic gain control (AGC) toggle | P1 | |
| E4 | Multiband compressor with 3 presets (Talk / Music / Loud) | P2 — **built** | Still preset-driven, not a full DSP panel |
| E5 | Graphic EQ | P2 — **built** | BUTT has 10-band; ours should be 3-band (low/mid/high) to stay simple |
| E6 | VST plugin hosting | P3 — **declined** | Against principle #1, and the licence is the smaller half: hosting means plugin scanning, a third-party editor window and surviving a plugin crash — an out-of-process subsystem whose failure mode is dead air |

### 3.6 Metadata (now playing)
| # | Feature | Pri | Notes |
|---|---|---|---|
| F1 | Manual "Now playing" text box with an Update button | P0 | |
| F2 | Read from a text file, polled (the automation-system standard) | P0 | Universal interop with playout software |
| F3 | Read from Windows media session (SMTC) — picks up Spotify, foobar2000, browsers | P1 | **This is the win**: BUTT can't do this on Windows |
| F4 | Remote metadata via local HTTP/TCP endpoint | P2 — **built** | For automation systems |
| F5 | Title formatting template (`{artist} - {title}`) + "don't send" toggle for ads/jingles | P2 — **built** | |

### 3.7 Recording
| # | Feature | Pri | Notes |
|---|---|---|---|
| G1 | Record while streaming, to the streaming codec or WAV | P0 | |
| G2 | Filename template with date/time tokens, configurable folder | P0 | |
| G3 | Auto-split by duration (hourly etc.) | P1 | |
| G4 | Low-disk-space warning and auto-stop | P1 | |
| G5 | Record-only mode (no server needed) | P1 | Lets people use SIRS as a plain recorder — good on-ramp |
| G6 | Auto start/stop on signal/silence | P2 — **built** | BUTT has it |

### 3.8 Reliability & connection
| # | Feature | Pri | Notes |
|---|---|---|---|
| H1 | Auto-reconnect with fast backoff (1s, 2s, 5s, 10s…) — no artificial delay | P0 | Direct dig at Rocket's 20s free-tier penalty |
| H2 | Buffered send with configurable buffer; keeps encoding during short outages | P0 | |
| H3 | Connection state machine surfaced clearly: Idle / Testing / Connecting / **LIVE** / Reconnecting / Failed | P0 | Big, unmistakable LIVE indicator |
| H4 | Listener count display where the server reports it | P1 | |
| H5 | Session log (connect/disconnect/errors) viewable in-app + written to file | P1 | Essential for supporting users remotely |
| H6 | Auto-connect on app start | P1 | |
| H7 | Bandwidth/dropped-frame stats | P2 — **built** | |

### 3.9 UI / UX / platform
| # | Feature | Pri | Notes |
|---|---|---|---|
| I1 | Single-window main UI, High-DPI correct | P0 | |
| I2 | First-run wizard: pick input → sound check → add server → test → go live | P0 | The 3-minute promise |
| I3 | Global hotkey for Connect/Disconnect and Mute | P1 | |
| I4 | Minimize to tray with live status in the tray icon | P1 | |
| I5 | Dark / light theme following Windows | P1 | |
| I6 | Keyboard navigation + screen-reader labels | P1 | Rocket does this; blind broadcasters are a real, underserved segment |
| I7 | Portable mode (config next to the .exe) alongside the installer | P1 | BUTT users expect this |
| I8 | Localization framework + English; community translations after | P2 — **built** | |
| I9 | Auto-update check | P2 — **built** | |
| I10 | CLI / remote control API | P3 — **built** | Both. Off by default, loopback unless deliberately opened, and opening it needs a password |
| I11 | MIDI CC control | P3 — **built** | Learn-by-moving-the-control. Everything below the driver is checked from raw bytes |

### 3.10 Explicit non-goals for v1

*(Reviewed at the end of Phase 4. VST hosting and cross-platform were confirmed as permanent
declines, with reasons, in §5. The rest stand as written.)*
Keeping these out is what makes SIRS simple. Say no on purpose:
- Full mixing console / more than 2 inputs
- VST hosting
- Playout, scheduling, playlists, or auto-DJ
- Video, WebRTC/WHIP
- Cross-platform (Windows first; revisit only after v1 lands)
- Any paid tier or feature gating

---

## 4. Recommended tech stack

**C# / .NET 8 + WPF, with NAudio for WASAPI and P/Invoke to native codec DLLs.**

- **UI:** WPF — mature, High-DPI, accessible (UI Automation for free, covers I6), fast to build. WinUI 3 is the modern option but still has rough edges around packaging and accessibility.
- **Audio capture:** NAudio (`WasapiCapture`, `WasapiLoopbackCapture`) covers A1 and A4 directly.
- **Encoders:** `libmp3lame`, `libopus`, `libvorbis`, `libFLAC` as bundled native DLLs via P/Invoke. All permissively licensed and patent-clear.
- **Networking:** `HttpClient` / `SslStream` for Icecast HTTP PUT and SHOUTcast sockets — no third-party dependency needed.
- **GC concern:** real, but manageable. Keep the capture→encode→send path on a dedicated thread with pre-allocated pooled buffers and no per-buffer allocation. The 1–2 s network buffer absorbs any gen-0 pause. This is not a low-latency monitoring path.

*Alternative if you want a leaner binary:* C++ with Qt (what BUTT uses) — better raw control, significantly slower to build the polished UI that is SIRS's entire point. I'd take the C# speed advantage.

**Licensing note:** ship MP3/Opus/Vorbis/FLAC freely. Do **not** ship AAC without buying into the patent pool.

---

## 5. Roadmap

> **Status:** Phase 0 was dropped at the user's direction. Phases 1, 2 and 3 are built, and Phase 4
> has been evaluated and decided: four of its six items are built, two are declined for reasons that
> are not going to change. See [README.md](README.md) for what works, what is verified, and — just
> as important — what is not.

### Phase 0 — Spike — *skipped*
Was: prove WASAPI → LAME → Icecast from a console before building UI. Skipped by decision; the
encoder layer is instead covered by an automated verification harness, and the source protocols
still need one live-server run.

### Phase 1 — MVP / v1.0 (the P0 set) — *built*
The complete "3-minute first run" story:
- Capture + device picker + gain (A1–A3)
- Meters, traffic-light coaching, Sound Check playback, monitoring, silence alert (B1–B5)
- Server profiles, paste-a-URL, auto-detect, Icecast + SHOUTcast + TLS, Test Connection (C1–C9)
- MP3 + Opus, quality presets, resampler (D1, D2, D5, D6, D8)
- Voice Enhance + safety limiter (E1, E2)
- Manual + text-file metadata (F1, F2)
- Recording with filename templates (G1, G2)
- Fast auto-reconnect, buffering, clear state machine (H1–H3)
- Single window, first-run wizard, High-DPI (I1, I2)

**Ship it here.** Get it in front of real broadcasters before adding anything.

> This line said "installer" for three phases and it was never true — there is no installer, no
> publish profile and no release. Portable mode (I7) works, but it has nothing to sit alongside.
> The instruction above was written after Phase 1 and then overtaken by Phases 2, 3 and 4; it is
> still the right instruction, and it is still the thing that has not been done.

### Phase 2 — v1.1 (P1) — *built*
Everything below is done. Original list: 2-source mixing (A5), device hot-plug recovery (A6), "Listen to my stream" (B7), host presets + profile import/export (C10, C11), Vorbis (D3), AGC (E3), Windows media session metadata (F3), record auto-split + record-only mode (G3–G5), listener count + session log + auto-connect (H4–H6), hotkeys, tray, theme, accessibility, portable mode (I3–I7).

### Phase 3 — v1.2 (P2) — *built*
Everything in the P2 set is done: multi-server / backup streaming (C12), lossless Ogg FLAC (D4), LUFS metering (B8), multiband compressor + 3-band EQ (E4, E5), remote metadata endpoint + title templates (F4, F5), channel-pair selection (A7), automatic on-air (G6), bandwidth and dropped-frame statistics (H7), localisation framework with English (I8), and an opt-in update check (I9).

Two things are worth reading as written rather than as ticked. The localisation *framework* is complete — packs, English fallback, coverage reporting, template export — but only part of the app's text currently goes through it; see [README.md](README.md) for exactly which. And the update check deliberately never downloads or installs anything, because an encoder that can replace its own binary is one that can be made to run someone else's code.

### Phase 4 — evaluate, don't commit (P3) — *evaluated and decided*

The point of this phase was to decide, not to build everything in it. Four items survived the
evaluation and were built; two were declined, and one non-goal was reconsidered and kept.

**Built:** ASIO input (A8), spectrum and stereo phase (B9), remote control endpoint and command
line (I10), MIDI control (I11).

**Declined — AAC (D7).** Not "later": the AAC patent pool charges per-unit royalties, and
FDK-AAC's licence explicitly grants no patent rights. There are only two ways to ship it, and both
are worse than not shipping it — pay per download, or put it behind a paywall and abandon principle
#6, which is the one real differentiator against Rocket. Opus already covers the quality need at
every bitrate a small station uses.

**Declined — VST hosting (E6).** The VST3 SDK being GPLv3-or-commercial is the smaller problem.
Hosting means scanning plugins, opening a third party's editor window inside SIRS, and surviving a
plugin that crashes — which honestly means an out-of-process host. That is a large subsystem whose
failure mode is "the show went off air", built for users who by definition already own a DAW.

**Declined — cross-platform.** WASAPI, DPAPI, the Windows media session, WPF, global hotkeys and
the tray icon are all Windows. This is not a port; it is a rewrite of everything that is not the
encoder. Revisit only if there is real demand, and then as a separate product decision.

Two things are worth reading as written rather than as ticked. ASIO capture is verified against real
hardware, but no broadcast has gone out from an ASIO input end to end. And MIDI is proven from raw
bytes with a real interface opening cleanly, but no physical fader has been moved — what a
particular desk sends is the kind of thing only hardware settles.

---

## 6. What to build first, concretely

1. **Phase 0 spike** — de-risks everything. If WASAPI→LAME→Icecast doesn't work reliably, nothing else matters.
2. **Server profile model + Test Connection** — build this before the pretty UI. The staged check (DNS → TCP → TLS → auth → mount → format) is the backbone of every good error message in the app.
3. **Sound Check (B3)** — record-and-play-back is technically easy and is the feature people will tell their friends about.
4. **First-run wizard** — assemble 1–3 into the guided flow. At this point you have a demo.
5. Everything else in P0.

The two things that must be excellent, because they're the reason to choose SIRS over free BUTT: **the sound test flow (§3.2)** and **server setup (§3.3)**. Spend disproportionate effort there.

---

## Sources
- [BUTT — Broadcast Using This Tool](https://danielnoethen.de/butt/)
- [Rocket Broadcaster](https://www.rocketbroadcaster.com/)
- [Rocket Broadcaster — Free Edition limitations](https://www.rocketbroadcaster.com/help/free-edition-limitations)
- [Rocket Broadcaster — Upgrade to Pro](https://www.rocketbroadcaster.com/pro)
- [Merlot Digital — Rocket Broadcaster FREE vs PRO](https://my.merlot.digital/knowledgebase/62/Rocket-Broadcaster-FREE-vs-PRO-Software-for-Relaying-through-us.html)
