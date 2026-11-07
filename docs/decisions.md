# Decisions

Non-obvious choices a reader could not predict from the code. Newest first.

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
