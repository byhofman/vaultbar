# Backlog

Every requirement, grouped by type. `## Next` is the slice being prototyped or built now.
IDs are permanent: never renumber or reuse one.

## Next

- [x] VBR-1 Run the macOS build on a real Mac; so far it is compiled and code-reviewed only
- [ ] VBR-2 Test organisation items against a real organisation vault (synthetic vault and SDK vectors covered; the real vault is a manual check)
- [ ] VBR-3 Touch ID unlock on macOS (built; a real finger on the sensor is a manual check)
- [x] VBR-4 Read the newer "v2" (COSE) key format
- [ ] VBR-7 Manual macOS check with Accessibility granted and a real vault: auto-type into another app, physical hotkey, tray click, screen lock and sleep
- [ ] VBR-8 Signed and notarized macOS dmg in the release, and a download page on the website

## Later

- [ ] VBR-5 Biometric unlock on Linux
- [ ] VBR-6 Accounts without a master password (SSO with trusted devices or key connector)
