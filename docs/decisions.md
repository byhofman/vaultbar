# Decisions

Non-obvious choices a reader could not predict from the code. Newest first.

## D-14 Browsers are matched by the loaded document's address — 2026-11-15

A page chooses its own title, so a look-alike page titled "Contoso: Sign in" used to get the real Contoso login offered
first and filled on Enter (H1). In a browser, Vaultbar now reads the URL of the page itself from the accessibility tree:
the value of the first on-screen document through UI Automation on Windows, the `AXURL` of the visible web area on
macOS. That is what was loaded, not the address-bar text (which may be half-typed) and nothing a page can set. Frames are
never read, hidden tabs are skipped by being off screen, and a window with two pages (a side panel) counts as unknown.
Matching is Bitwarden's own `LoginUriView.matchesUri`: the six match options, the account's default and equivalent
domains from `data.json`, the google.com/script.google.com exclusion, and base domains by the Public Suffix List with the
private section, as tldts does it. The list (MPL-2.0, publicsuffix.org) is embedded, because Vaultbar never fetches
anything but icons; it is refreshed with the source like any other dependency. When the address is unknown or not the
item's, the footer shows the host and a second Enter fills, and only for that host: if the page moved meanwhile, it asks
again. On macOS auto-type also stops when another window of the same app takes focus. Browsers are known by process name
or bundle identifier, and any other app whose window shows an http(s) page (an unlisted browser, a web view) is treated
as one after a quick look. Firefox on macOS gives its page no size, which the read cannot tell from a hidden tab, so its
address stays unknown and every fill there asks. Linux reads no address yet, so browsers there get no suggestions and
always ask. Other apps keep title matching without a second Enter, as the review proposed; the residual is an app whose
title someone else writes, such as a terminal connected to a remote host.

## D-13 Tmds.DBus.Protocol comes from Avalonia only — 2026-11-15

Dependabot bumped the direct Tmds.DBus.Protocol reference from 0.21.3 to 0.95.2, and the build broke on all three
systems because 0.93 removed `Connection`. Porting the session-lock code to the new API would only have moved the break:
Avalonia 11.3 is compiled against 0.21.3, so its Linux backend would load a library without the types it calls. The app
now has no reference of its own and takes the version Avalonia brings (0.21.3 is the patched release of GHSA-xrw6-gwf8-vvr9),
and a test fails if the shipped copy ever differs from the one Avalonia was built against. Moving to 0.9x comes with
Avalonia 12, which needs 0.94.

## D-12 Installers look up the release with GITHUB_TOKEN when it is set — 2026-11-14

The installers read the release from the GitHub API to get the asset digest. Anonymous API calls are limited to 60 an
hour per IP address, and GitHub-hosted macOS runners share egress addresses: 6 of 12 runners probed on 2026-11-14 got
HTTP 403 with the quota at 0, which made the macOS installer test fail at random. CI now passes its own token, and the
scripts send it only to the API (from a header file, so it stays out of the process list), never to the download.
Users behind a shared address can do the same; everyone else needs nothing.

## D-11 Copy keys follow 1Password, not the earlier table — 2026-11-13

The README promised 1Password's keys but had them backwards: 1Password's Quick Access copies the username with Ctrl+C
and the password with Ctrl+Shift+C ([its shortcut list](https://support.1password.com/keyboard-shortcuts/)). Vaultbar
now does the same, since switching users are the ones it is for, and a password pasted where a username was expected is
the worse mistake. Alt+Enter opens the website as in 1Password. Ctrl+O keeps opening the website (1Password: item
details in a new window), because Tab already shows the details and changing it would only move the surprise.

## D-10 Secrets are short-lived strings, not pinned buffers — 2026-11-13

Keys live in byte arrays that locking zeroes. Passwords, codes and notes stay encrypted until a copy, fill or reveal, and
then exist as ordinary .NET strings for that moment: the clipboard, SendInput, CGEvent and the text box all take strings,
and .NET advises against SecureString. What is enforced instead is that nothing keeps them: the clipboard is recognised
by a salted hash, locking drops every row and revealed field, and the card list decrypts only the last four digits.

## D-9 Linux locks on the screensaver and logind signals — 2026-11-13

`lockOnSystemLock` did nothing on Linux. Vaultbar now listens for `ActiveChanged` from org.freedesktop.ScreenSaver and
org.gnome.ScreenSaver on the session bus, and for logind's `Lock` (this session only) and `PrepareForSleep` on the system
bus, through Tmds.DBus.Protocol at the version Avalonia already ships, so no new dependency enters the build.

## D-8 The single-instance lock and socket moved out of /tmp — 2026-11-13

On macOS and Linux the named mutex lived in .NET's shared folder under /tmp and the socket in /tmp itself, both with
names anyone could guess and take first. Both now sit in a folder only the user can open (`$XDG_RUNTIME_DIR/vaultbar`,
else the cache folder). Windows keeps a session-local mutex and a named pipe: pipe names are machine-wide there, so
another local user could take the name first and stop Vaultbar starting, but cannot talk to it.

## D-7 Installers check GitHub's own digest of each asset — 2026-11-13

GitHub records a SHA-256 digest for every release asset, so the installers read it from the API and refuse a download
that differs. A SHA256SUMS file would come from the same release, would be missing from the releases already out, and
adds nothing over it. Each release also gets a signed build-provenance attestation for anyone who wants to check the
build itself.

## D-6 The dmg ships ad-hoc signed until a Developer ID exists — 2026-11-07

Same as Dockyard: a browser download needs Open Anyway once, and that beats no download for the website to link to. The
signing and notarization steps are in `packaging/macos/package.sh` and switch on with the `APPLE_*` secrets, on tags only.
They have never run, because no certificate exists yet; the entitlements are the standard .NET pair and may need
adjusting on the first real run. The installer now keeps a Developer ID signature instead of re-signing ad hoc. The bundle
identifier moved from `local.vaultbar` to `dev.byhofman.vaultbar` before anyone depends on the old one.

## D-5 v2 keys read both COSE ciphers and refuse every mismatch — 2026-11-07

Bitwarden's SDK now has XAES-256-GCM next to XChaCha20-Poly1305 for type 7 EncStrings, so both are read: an account on the
newer one would otherwise be locked out with no warning. As in the SDK, the key's algorithm and key ID must match the
message, and a content type it does not know is refused. A type 0 EncString is now only accepted by a key without a MAC,
also as in the SDK: otherwise anyone able to edit `data.json` could strip the MAC from a value. .NET has no extended-nonce
ChaCha20, so HChaCha20 is implemented here and checked against the draft's test vector. CBOR uses Microsoft's
`System.Formats.Cbor`.

## D-4 Touch ID uses a Secure Enclave key blob through a Swift library — 2026-11-07

The data-protection keychain needs an entitlement an ad-hoc signed app cannot hold (and a provisioning profile), and the
file-based keychain would gate the key only by a dialog and ask again after every update re-signs the app. CryptoKit's
Secure Enclave keys avoid both: the key blob can sit in a file because it only works inside this Mac's enclave, and it
survives re-signing. CryptoKit has no C API, so `src/Vaultbar.TouchId` is a small Swift library and macOS builds moved to
the macOS CI job. The key agreement output is deterministic, which fits the same `IQuickUnlockProvider` Windows Hello uses.
Only codes that mean the key is gone forget the enrollment: a closed lid or a canceled prompt must not.

## D-3 A damaged account private key does not stop the unlock — 2026-11-07

Organisation keys are unwrapped with the account's RSA private key. If that key does not decrypt, the personal vault
still opens and the organisation items count as failed, because they are a part of the vault and the footer already
reports failed items. Items of provider-managed organisations take the same path: reading provider keys is left out
until someone needs it, since it could not be checked against a real provider.

## D-2 Cmd+Q clears the clipboard without cancelling — 2026-11-05

Cmd+Q and logout reach the lifetime's ShutdownRequested, not `Quit`. The handler clears a copied secret synchronously
and lets the shutdown continue. Cancelling it to run `Quit` would answer NSTerminateCancel, which aborts a logout.

## D-1 macOS end-to-end drives the real backend in-process — 2026-11-05

`tests/e2e-macos` starts the app in its own process with the real Avalonia native backend and `MacPlatform`, and sends
NSEvents to its own window. That needs no Accessibility permission and cannot type into another app. Its limits are
deliberate: the clipboard is a private named pasteboard, opening URLs is recorded, the lock notification has a private
name because the real one would also lock Bitwarden, and typing into another app is left to the manual check (VBR-7).
It refuses to run outside a throwaway `HOME`.
