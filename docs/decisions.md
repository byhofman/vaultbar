# Decisions

Non-obvious choices a reader could not predict from the code. Newest first.

## D-18 Installers verify the signer once one exists, and the attestation until then — 2026-11-15

The digest check proves integrity, not origin, because the digest comes from the same release (H2). The installers now
carry the expected signer as a constant: the Apple Team ID in `install-macos.sh` and the Authenticode subject in
`install.ps1`, both empty until VBR-8 and Windows signing deliver one. Set, they refuse an app whose signature does not
verify, is not that Team ID or subject, or (macOS) that Gatekeeper rejects, and they stop stripping quarantine,
re-signing ad hoc and running `Unblock-File`. Empty, they say "Unsigned build" and, when GitHub CLI is signed in, run
`gh attestation verify` pinned to this repository, the release workflow, the tag and GitHub-hosted runners, failing
closed. v0.3.1 and earlier predate attestations, so for those only the digest is checked; a local `--archive` is the
user's own file and gets a pointer to the command. The test hooks `VAULTBAR_EXPECT_TEAM_ID` and `VAULTBAR_EXPECT_SIGNER`
only apply while the constant is empty, so they can switch the check on but never off. Windows now builds on the Windows
runner, where Azure Artifact Signing (the account set up on 2026-11-07, OIDC, no secret) signs on a tag once
`SIGNING_ENABLED` is true; pipeline-templates' `sign-windows` cannot be used because that repository is private and
this one is public, so the same two actions are called directly, pinned by SHA.

## D-17 The review's low findings, how each was closed — 2026-11-15

L1: key buffers, the quick-unlock sealing key among them, are allocated on the pinned object heap and locked in RAM
(VirtualLock/mlock), so zeroing reaches the only copy and keys are not paged out; pages stay locked for the process
because pages are shared between keys. The bar is excluded from Windows Error Reporting and has a zero core-dump limit
elsewhere. Strings for a copy or fill stay ordinary strings (D-10). L2: a fill that cannot run never copies the password
by itself; the footer names the copy key. L3: re-prompt asks for the master password, as Bitwarden does;
`repromptWithQuickUnlock` lets the device confirm. L4: one Windows Hello key per account (`vaultbar-` and a hash of the
account ID), created with FailIfExists and then opened, the old shared key deleted; the Hello dialog is only raised when
its window belongs to System32\CredentialUIBroker.exe. L5: the Windows pipe name carries 128 random bits kept in the
user's own folder. L6: `global.json` pins the 10.0.4xx SDK band with the latest patch, Dependabot bumps it, and a weekly
workflow fails when a .NET security release is newer than the latest Vaultbar release. No "update available" line in
the app: it would be network traffic besides icons. L7: an icons address is used only on Bitwarden's icon hosts or the
host of the account's own server; any other host falls back to that server or Bitwarden's cloud.

## D-16 Quick unlock lives as long as the process; across restarts only by opt-in — 2026-11-15

The user key used to sit on disk, wrapped under the device key, with no lifetime: after a reboot Windows Hello or Touch
ID alone opened the vault, even after the account was signed out of the desktop app (M2). Bitwarden asks for the
master password after a restart by default ([help](https://bitwarden.com/help/biometrics/)). Now locking keeps the key
in memory, sealed under a random per-process key held beside it (so it is a lock against nothing but a casual read of
memory, as Bitwarden's in-memory biometric key is), and the disk holds only the challenge and a verifier: an HKDF of the
device signature bound to the account and the user key, so a quick unlock is accepted only when the device signs to
the same value and the key it opens is the enrolled one. `quickUnlockAcrossRestarts` (off by default; recorded here as
the opt-in the finding asks for) adds the wrapped key on disk, with the master-password time it dates from bound in as
associated data, valid for `quickUnlockMaxAgeDays` (14, clamped to 1-365) after that unlock and never for a time more
than a day ahead. The same age ends the in-process key, so the master password is asked for at least that often. A
quick unlock never extends it, and the key is re-stored at the next quick unlock after a password unlock, so no extra
prompt appears. Turning the option off strips the key at the next read. An account missing from
`global_account_accounts`, a sign-out of the last account, or a deleted `data.json` removes the enrollment and the
in-memory key; an account switch drops the in-memory key. Enrollments from before this change carry no age, so they are
deleted the first time Vaultbar looks at them, and Vaultbar offers to set quick unlock up again.

## D-15 Bitwarden's vault timeout, value by value — 2026-11-15

Only a positive number of minutes used to count; "Immediately", "On system idle", "never" and the legacy negatives all
meant no idle lock (M4). Now: minutes as set; `0` locks when the bar closes; `onIdle`/`-4` lock after 5 minutes without
input anywhere (Bitwarden's own threshold; the bar's idle time stands in where the system does not say); `onSleep`,
`onLocked` and their negatives lock on sleep and screen lock even with `lockOnSystemLock` off. `0`, `onSleep` and
`onLocked` keep the 15-minute idle lock as a backstop, since the vault can be unlocked while the bar is hidden and the
Linux session-lock signals may be missing. `onRestart`, `never`, `custom` and a missing value lock after 15 idle minutes:
Vaultbar runs for weeks, so "until restart" would mean the same as never. `lockAfterMinutes` still overrides everything,
with `0` as the explicit way to turn automatic locking off. Idle time now counts while the bar is open, from the last key
or click in it. Under "Immediately", a secret copied in that opening stays on the clipboard for its clear timer, because
it was copied to be pasted; with no timer it is cleared at once.

## D-14 Browsers are matched by the loaded document's address — 2026-11-15

A page chooses its own title, so a look-alike page titled "Contoso: Sign in" used to get the real Contoso login offered
first and filled on Enter (H1). In a browser, Vaultbar now reads the URL of the page itself from the accessibility tree:
the value of the first on-screen document through UI Automation on Windows, on macOS the `AXURL` of the outermost web
area above the focused element (the page the keystrokes go to), else of the only visible web area. That is what was loaded, not the address-bar text (which may be half-typed) and nothing a page can set. Frames are
never read, hidden tabs are skipped by being off screen, and a window with two pages (a side panel) counts as unknown.
Matching is Bitwarden's own `LoginUriView.matchesUri`: the six match options, the account's default and equivalent
domains from `data.json`, the google.com/script.google.com exclusion, and base domains by the Public Suffix List with the
private section, as tldts does it. The list (MPL-2.0, publicsuffix.org) is embedded, because Vaultbar never fetches
anything but icons; it is refreshed with the source like any other dependency. When the address is unknown or not the
item's, the footer shows the host and a second Enter fills, and only for that host: if the page moved meanwhile, it asks
again. On macOS auto-type also stops when another window of the same app takes focus. Browsers are known by process name
or bundle identifier, and any other app whose window shows an http(s) page (an unlisted browser, a web view) is treated
as one after a quick look; on Windows only when that page is web content (UIA framework Chrome or Gecko), since in
any other app a document holds its own text, which a file could fill with an address. Firefox on macOS gives its page no size, which the read cannot tell from a hidden tab, so its
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
