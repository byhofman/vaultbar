# Decisions

Non-obvious choices a reader could not predict from the code. Newest first.

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
