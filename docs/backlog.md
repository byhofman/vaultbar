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
- [ ] VBR-18 Branding pass: Vaultbar's logo colour as a small hint in the interface (owner 2026-11-14: "branding is v1 for every app, as well is feedback")
- [ ] VBR-19 Feedback from inside the app: support, a feature request or a bug report through the platform's feedback client (ACC-147), without an account where the app has none, and never sending the user's files (owner 2026-11-14: "branding is v1 for every app, as well is feedback")
- [x] VBR-20 Research: is the current Bitwarden integration secure? How Vaultbar talks to Bitwarden (CLI, API or SDK), where the session key, unlock state and cached items live in memory and on disk, what Windows Hello and Touch ID actually protect, the clipboard and auto-type exposure, the IPC between the bar and its helper, update signing, and how this compares with Bitwarden's own clients; findings ranked, each with a fix (owner 2026-11-14, v1) Done 2026-11-14: docs/research/security-review/findings.md (no Critical; H1, H2 before v1).
- [ ] VBR-21 Monitoring (owner 2026-11-14, v1): the download page and the release downloads are watched for being up, with an alert; no telemetry from the app itself
- [ ] VBR-22 H1: match suggestions and auto-type on the browser's address with Bitwarden's URI rules, never on a browser window title alone; confirm with the host shown when the address is unknown or does not match (security review 2026-11-14, docs/research/security-review/findings.md; v1)
- [ ] VBR-23 H2: the installers verify the release signer (Team ID / Authenticode subject) and stop stripping quarantine and re-signing; until signing exists, verify the build attestation when gh is present and say "unsigned build" (with VBR-8 and Windows signing) (security review 2026-11-14, docs/research/security-review/findings.md; v1)
- [ ] VBR-24 M1: rulesets on main and v* tags and a release environment for the signing credentials (repository settings, owner's call) (security review 2026-11-14, docs/research/security-review/findings.md; v1)
- [ ] VBR-25 M2: quick unlock per process by default; persistence across restarts opt-in with a maximum age; forget it when the account leaves data.json (security review 2026-11-14, docs/research/security-review/findings.md; v1)
- [ ] VBR-26 M3: the macOS Secure Enclave key in the data-protection keychain under Vaultbar's access group once a Developer ID exists; document the Windows residual (security review 2026-11-14, docs/research/security-review/findings.md; v1)
- [ ] VBR-27 M4: map every Bitwarden vault-timeout value correctly (0, onIdle, onSleep, onLocked, onRestart, never, legacy negatives) and count idle time while the bar is open, with tests (security review 2026-11-14, docs/research/security-review/findings.md; v1)
- [ ] VBR-28 L1-L7 from the review: pinned and locked key memory; no silent fallback copy; master-password re-prompt by default; per-account Hello key; random pipe suffix; runtime update check; favicon host allow-list (security review 2026-11-14, docs/research/security-review/findings.md; v1)
