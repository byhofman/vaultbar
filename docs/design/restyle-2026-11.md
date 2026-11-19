# Vaultbar polish, November 2026

Vaultbar in the byhofman restyle round. The owner's direction: "Polish, not a full restyle." The bar keeps
its layout, type (Inter), components and keys. The changes are targeted:

- the logo's colours are clearly present;
- the selected row takes a wash instead of the yellow bar on its left;
- text passes WCAG AA in both themes, and the dark bar stands clear of a dark desktop;
- one 20 px gutter, so icons and text line up;
- the visual bugs below are fixed.

Behaviour is unchanged.

## How the shots were made

Screenshots are in [`restyle-2026-11/before/`](restyle-2026-11/before/) and
[`restyle-2026-11/after/`](restyle-2026-11/after/). They are named `<nn>-<state>-<light|dark>.png` and are
712 px wide at 1×. The before shots are of `main` at `ce77693`.

These are **not** `screencapture` shots. Two things stand in the way:

1. The bar sets `NSWindowSharingNone` (`hideFromScreenCapture`, on by default), so macOS leaves it out of
   every capture. That can be turned off in the config.
2. The session that made these shots had no Screen Recording permission and no window-server access
   (`screencapture` failed with "could not create image from rect", and Avalonia.Native could not start
   its display link). Granting that permission is the owner's decision, so it was not done.

So the shots come from the real `QuickWindow` and `App.axaml`, rendered by Avalonia's headless Skia
backend. The vault is the synthetic `VaultFixture.Demo()` with its throwaway `demo-password`, in a
throwaway `HOME`. It touches no Bitwarden data, Keychain or real credential. The fixture also adds one
login with a very long name, to check truncation. The harness drives the window's own methods (unlock,
search, `Move`, `OpenDetailsAsync`, `ShowHelp`) and renders it with `RenderTargetBitmap`, which is how
`tests/e2e-macos` already takes its shots. Headless mode reports no window transparency, so the harness
puts back the 16 px margin and 12 px corners that macOS gets. The backdrop behind the bar is a flat grey,
with `#2B2D31` in dark. The macOS window shadow is not drawn.

| # | State |
|---|---|
| 01 | Locked: master password |
| 02 | Empty search: Favorites, first row selected |
| 03 | Results for "contoso" |
| 04 | A result selected further down (third row) |
| 05 | A name too long for the row |
| 06 | Item details (GitHub) |
| 07 | Item details with the one-time code selected |
| 08 | No match |
| 09 | Keyboard shortcuts |

## Before

| Area | Honest critique |
|---|---|
| **Brand** | The blue of the mark was already the accent, but it showed only on the header search icon, the caret and toasts. The yellow appeared only as the 3 px bar on the selected row (D-20). The favourite star used its own hard-coded amber (`#E0A100`), which is close to the logo's yellow but is not it. |
| **Selection** | The selected row had a pale wash plus a 3 px yellow bar on the left, drawn as a left border. That is the outline-on-selection the owner calls dated. On the 8 px rounded row, the bar also bent around the corners. |
| **Contrast** | The muted subtitle on the selected row failed AA: 4.18:1 in light and 4.06:1 in dark. The star was 2.27:1 on white, below the 3:1 for icons. Fluent draws the search watermark ("Search Bitwarden", "Master password") at half opacity, so it fell well below AA in both themes. |
| **Dark lift** | The dark surface `#1F242C` against a dark desktop (`#1E1E1E`) was 1.07:1. Its `#353C47` border was 1.5:1 against the desktop, and the shadow was one fixed `#55000000` for both themes, so the bar barely stood out on a dark background. |
| **Spacing** | The left edges were 32 px (header icon), 36 px (row tiles and section labels), 38 px (detail labels) and 40 px (messages and shortcuts) from the bar's edge. The footer used 16 px, the header 16 px on the left and 18 px on the right. |
| **Focus** | Focus stays in the search box, and the selected row is where the keyboard is. Without the bar, the wash alone was too faint to find at a glance. |

## What changed

All colours stay in the two theme dictionaries in `App.axaml`. `QuickWindow.axaml` no longer has a colour
literal: the star and the shadow were the last two.

| What | Change |
|---|---|
| **Selection** | No border or bar. The selected row sits on a stronger brand-blue wash (light `#DFE9FD`, dark `#2A3C64`), and its chevron turns the accent blue. That gives the row a clear "you are here" without an outline. Hover stays a neutral grey, so hover and selection never look alike. The selected field in item details takes the same wash. |
| **Logo colours** | Blue is the accent as before: light uses the deep stop of the mark's gradient (`#175DDC`), dark uses a lighter blue (`#82A9FF`). It also drives the selection wash and the selected chevron. The yellow moves from the selection bar to two places that mean something. (1) The **lock** in the header on the unlock, re-prompt and error screens, because it is the key of the mark. (2) The **favourite star**. Dark uses the logo's yellow exactly (`#F4C542`). Light deepens it to `#A97A00`, the way the light icon deepens its colours, because `#F4C542` is only 1.6:1 on white. |
| **AA text** | Light muted goes from `#667285` to `#5B6678`: 5.8:1 on white, 5.4:1 on the footer, 4.8:1 on the wash. Dark muted goes from `#8F9AAB` to `#A3ADBC`: 6.3:1 on the surface, 4.8:1 on the wash. The search and password watermarks are drawn at full opacity in the muted colour. |
| **Dark lift** | The surface goes from `#1F242C` to `#262B34` and the footer from `#191D24` to `#20242B`. The border becomes a lit edge, `#4A5363` (2.15:1 against a `#1E1E1E` desktop, was 1.5:1). The shadow is a theme token: soft in light (`#38000000`) and deeper in dark (`#99000000`). The wash, hover, key-cap and tile fills move up with the surface. |
| **Spacing** | One 20 px gutter from the bar's edge: header icon, row tiles, section labels, detail labels, the back chevron, messages, shortcuts and footer hints. The header status sits 20 px from the right edge, the same as the row chevrons. Result and detail rows use 12 px of padding on both sides now that the 3 px border is gone, so nothing shifts when a row is selected. |
| **Tests** | `BrandingTests` replaces the old check that the brand token is the logo's yellow in both themes. (1) The light accent is a stop of the mark's gradient and the dark brand is the mark's yellow. (2) Text and muted text pass 4.5:1 on the surface, footer, wash and hover in both themes, and so do the accent, error and key-cap text. (3) The brand yellow and the accent pass 3:1 as icons on the surface, wash and hover. (4) `QuickWindow.axaml` has no colour literals. Against `main`'s tokens, three of these fail. |

Checked on every after shot, in both themes: no text is clipped. The long name ends in an ellipsis before the
chevron. The footer hints fit on the widest set (search with a one-time code). The status ("17 items · synced
3 min ago") does not collide with the query.

## After

| | Before | After |
|---|---|---|
| Selected row | Wash + yellow left bar | Brand-blue wash + blue chevron, no border |
| Muted on selected | 4.18 / 4.06 (fail) | 4.76 / 4.80 |
| Star on white | 2.27 (fail) | 3.84 |
| Watermark | half opacity (fail) | full muted, 5.8 / 6.3 |
| Dark edge vs dark desktop | 1.50 | 2.15, plus a deeper shadow |
| Left edges | 32 / 36 / 38 / 40 px | 20 px everywhere |

## For the owner

1. **Where the yellow lives.** D-20 put the logo's yellow on the selection bar, and you have now ruled bars
   out. This polish moves it to the lock (unlock and re-prompt) and to the favourite star. The other option
   is to give the yellow no part at all and let the blue carry the brand alone. The shots show the lock and
   star version.
2. **The light star is ochre, not the logo's yellow.** `#F4C542` cannot reach 3:1 on white. Choose between
   the deepened `#A97A00` (as shipped here) and the logo's yellow with a thin dark outline.
3. **Real macOS captures.** To replace these renders with `screencapture` shots, give the terminal Screen
   Recording permission, then set `"hideFromScreenCapture": false` in a throwaway config. The bar hides
   itself from capture on purpose, so keep that setting on in daily use.
