# Backlog

Every requirement, grouped by type. `## Next` is the slice being prototyped or built now.
IDs are permanent: never renumber or reuse one.

## Next

- [x] VBR-1 Run the macOS build on a real Mac; so far it is compiled and code-reviewed only — v0.3.0
- [ ] VBR-2 Test organisation items against a real organisation vault (synthetic vault and SDK vectors covered; the real vault is a manual check)
- [ ] VBR-3 Touch ID unlock on macOS (built; a real finger on the sensor is a manual check)
- [x] VBR-4 Read the newer "v2" (COSE) key format — v0.3.0
- [ ] VBR-7 Manual macOS check with Accessibility granted and a real vault: auto-type into another app, physical hotkey, tray click, screen lock and sleep
- [ ] VBR-8 Signed and notarized macOS dmg in the release, and a download page on the website (dmg and signing steps built; needs the Apple credentials, and the website has no pages yet)

## Security

- [x] VBR-9 Lock when the Linux session locks or the machine suspends
- [x] VBR-10 Installers check each download against its SHA-256 digest, and releases carry build provenance
- [x] VBR-11 Auto-type stops the moment another window takes focus
- [x] VBR-12 Copied secrets never reach other devices or clipboard history, and are cleared on lock even with the timer off
- [x] VBR-16 A test keeps "the only network traffic is website icons" true

## Switching from 1Password

- [x] VBR-13 Copy keys work as in 1Password Quick Access: Ctrl+C username, Ctrl+Shift+C password, Alt+Enter opens the website
- [x] VBR-14 A README guide for 1Password users: how to import, and a key map checked against 1Password's own list

## Keyboard

- [x] VBR-15 The shortcut list (F1) shows every key, Bitwarden's included

## Later

- [ ] VBR-5 Biometric unlock on Linux
- [ ] VBR-6 Accounts without a master password (SSO with trusted devices or key connector)
- [ ] VBR-17 English and Dutch UI, following the system language
