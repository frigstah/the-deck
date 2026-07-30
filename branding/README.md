# Branding

The Deck's mark is **Plate**: a D with its curve replaced by two 45-degree cuts, and a counter big
enough to hold a lamp.

<img src="plate/icon-256.png" alt="" width="96" height="96">

It was picked from five routes drawn and compared at true pixel size in the surfaces they have to
survive — the 78-pixel block at the head of the rail, the deck, mini mode, the taskbar, the
notification area. [`identity-routes.html`](identity-routes.html) is that comparison, kept because the
four that were not chosen are the argument for the one that was. The other four marks are still in
their folders as vector source.

| Route | The idea | Reads as |
| --- | --- | --- |
| **`plate`** ✔ | A D whose curve is two 45-degree cuts, with a counter that holds the on-air lamp. | A letter D — a product |
| `lamp` | A plate with the lens cut out, and two ticks of legending. The lamp outside a live studio. | A lamp, or a record button |
| `levels` | The two channels of the meter that takes up half the deck. Six segments lit over four. | Levels, or signal strength |
| `faders` | Two channel faders at different positions — why the app is called The Deck. | A mixer, or a settings slider |
| `carrier` | One stroke describing a square wave, stepping up as it leaves the frame. | A square wave, digital audio |

Plate won on two things. It is a letterform, so it reads as a particular product rather than as a
category — none of the others stops it being mistaken for a media player or a meter. And it is the
only one of the five where the state indicator is part of the letter rather than stuck on it.

## Where the geometry lives

Not here. [`src/Deck.App/DeckMark.cs`](../src/Deck.App/DeckMark.cs) is the single definition, as a
table of hand-set cuts in whole pixels — one row per size, giving the position, the width and height,
the stem thickness and the chamfer. Everything that draws the mark reads that table: the tray icon at
runtime, the `.ico` the build embeds, and the lock-up in the title bar.

The SVGs in each route folder are the presentation drawings on a 64-unit grid, not the source of
truth. Colour in them comes from three custom properties so one file serves every context:

| Property | Default | Used for |
| --- | --- | --- |
| `--deck-ink` | `#16191C` | The form. Set to `#F0F2F0` on a dark ground. |
| `--deck-accent` | `#2A6A70` | The accent detail. `#5FB6B4` on the dark theme. |
| `--deck-lamp` | `transparent` | The on-air element. `#C93F36` live, `#E0A32E` connecting. |

## Regenerating the icon

```
dotnet run --project branding\IconGen
```

`IconGen` is deliberately not in `Deck.sln` — it runs by hand when the mark changes, and everything it
writes is committed, so an ordinary build never needs it. It compiles `DeckMark.cs` by link rather than
copying the geometry, because two copies of a mark is how an icon and an application end up drawing
slightly different letters.

It writes:

| File | What for |
| --- | --- |
| `src/Deck.App/Deck.ico` | Nine sizes: 16, 20, 24, 32, 40, 48, 64, 128, 256. Taskbar, Alt-Tab, Explorer, the window, and the installer. |
| `plate/icon-256.png`, `icon-512.png` | The readme and the release page. |
| `plate/mark-ink-512.png`, `mark-light-512.png` | The mark alone, transparent, for either ground. |
| `plate/icon-512-live.png` | With the lamp lit, for anything large enough to show it. |
| `plate/hinting-proof.png` | Every small size magnified with a pixel grid over it. |
| `installer/wizard-small.bmp` | The mark on the setup wizard's pages. |

Then it reads the finished icon back through WPF's decoder and checks every frame. That is a different
parser from the one that wrote it, on purpose: a hand-built container that is subtly wrong still opens
in some viewers and then turns up as a blank square in a taskbar. `System.Drawing` is no good for the
job — it cannot see a 256-pixel entry at all and quietly hands back the 128 one instead, which made
the check pass for the wrong reason until it was noticed.

**Look at the proof sheet after regenerating.** It is the only place hinting can actually be judged,
and it has already earned its keep: 16 had half the vertical margin of every other size, so it read as
a different, more cramped icon, and 24 came out exactly square so the D looked squat beside its
neighbours. Both were invisible at true size and obvious at 8×.

## Where it is wired in

| Place | How |
| --- | --- |
| Taskbar, Alt-Tab, Explorer, the window | `ApplicationIcon` in `src/Deck.App/Deck.App.csproj`. The window sets no icon of its own, so it falls back to the executable's — one icon rather than two that can drift apart. |
| Notification area | `TrayPresence.BuildIcon`, drawing `DeckMark` at whatever size Windows asks for, in the state colour. |
| Title bar | `DeckMark.TitleBarGeometry`, drawn as a `Path` beside the wordmark in `MainWindow.xaml`. |
| Installer | `SetupIconFile` and `WizardSmallImageFile` in `installer/Deck.iss`, plus `UninstallDisplayIcon` so Add or remove programs shows it too. |

`tests/Deck.EncoderCheck` carries eight checks over the committed icon, including that the project file
still refers to it at all.

## Still text, not outlines

The wordmark is Consolas Bold in the title bar and Segoe UI Variable Display in the presentation, both
of them live text. At thirteen points, hinted type is sharper than any outline of it would be, so the
title bar is right as it stands. What is missing is a drawn wordmark for large use — a readme header, a
splash, an installer banner — where an outline would be identical on every machine and live text is at
the mercy of what is installed. That is the next piece of this if it is wanted.
