# Is Vaultbar's Bitwarden integration secure? (VBR-20)

Reviewed 2026-11-14 against `main` at 49385f3 (v0.3.1 plus the installer token fix). Scope: Windows and macOS. Method: all of `src/`, `install/`, `packaging/` and `.github/` read, plus README, docs/backlog.md and docs/decisions.md; the design compared with Bitwarden's own clients (source, help pages, white paper); one local experiment on a throwaway Secure Enclave key (M3). No real vault, account or secret was used.

## Verdict

**No Critical findings: secure enough to keep shipping as a beta for people who already run the Bitwarden desktop app. Fix H1 and H2 before calling it v1 or promoting it to people coming from 1Password.**

The core is sound and in places stricter than Bitwarden's own desktop app: it never talks to Bitwarden (no CLI, no `BW_SESSION`, no API key), decrypts with the official scheme and refuses a downgrade from authenticated to unauthenticated data, gates quick unlock by hardware (TPM-backed Windows Hello signature, Secure Enclave key needing a touch each time) rather than a yes/no dialog, and handles the clipboard properly. The weak spots are at the edges: auto-type trusts the window title, releases are unsigned and the installers strip the OS checks, quick unlock lives longer than Bitwarden's, and the idle lock misreads some of Bitwarden's timeout settings.

| # | Severity | Finding |
| --- | --- | --- |
| H1 | High | Suggestions and auto-type match the window title, which a phishing page controls |
| H2 | High | Releases are unsigned and the one-liners strip Gatekeeper/SmartScreen; the digest check proves integrity, not origin |
| M1 | Medium | `main` and `v*` tags are unprotected; the one-liners execute whatever is on `main` |
| M2 | Medium | Quick unlock keeps the user key on disk with no lifetime: after a reboot Hello/Touch ID alone opens the vault |
| M3 | Medium | Any same-user process can use the Touch ID key blob and the Windows Hello key (a fake prompt away) |
| M4 | Medium | Idle lock: Bitwarden's "Immediately", "On idle", "never" and the legacy negative values mean no idle lock |
| L1 | Low | Keys and decrypted strings sit in movable, pageable managed memory; zeroing cannot reach GC copies |
| L2 | Low | Auto-type keystrokes are visible to keyboard hooks; a failed fill silently copies the password |
| L3 | Low | Re-prompt accepts Windows Hello/Touch ID; Bitwarden asks for the master password |
| L4 | Low | Windows Hello key name and dialog focusing can be interfered with by same-user apps |
| L5 | Low | Windows pipe name can be squatted by another local user (startup DoS, D-8) |
| L6 | Low | The bundled .NET runtime is only patched by a new release; nothing tells users to update |
| L7 | Low | Whoever can edit `data.json` can redirect favicon requests and learn which sites are in the vault |
| I1–I6 | Info | Integration model, crypto, clipboard, IPC, logs, dependencies are fine |

## 1. How Vaultbar talks to Bitwarden

No CLI, API or SDK: it reads the desktop app's encrypted `data.json` and decrypts locally (`src/Vaultbar/Vault/DesktopStore.cs:9-63`).

- **Master password:** typed into the bar (`UI/QuickWindow.axaml:31`), run through the account's KDF (PBKDF2-SHA256 or Argon2id; `Vault/MasterKey.cs:9-35`), HKDF-stretched, and used to unwrap the user key (`VaultService.cs:239-254, 331-343`). Password bytes and master key are zeroed (`MasterKey.cs:33`, `VaultService.cs:252`); the text-box string is not (D-10). Never stored, logged or passed on.
- **`BW_SESSION` / API keys:** none. The CLI's session key is handed around by env var or `--session` until `bw lock` ([CLI docs](https://bitwarden.com/help/cli/)); Vaultbar has nothing comparable to leak.
- **User key:** memory only while unlocked (`VaultService.cs:19, 291-307`), zeroed with all organisation and item keys on lock (`VaultService.cs:309-329`). Locks on idle (see M4), Ctrl+L, tray, `vaultbar lock`, session lock and sleep (`AppController.cs:62-77`).
- **Persisted secret:** quick unlock only: the user key, AES-256-GCM-wrapped under a key derived from a device-bound signature, in `quick-unlock.dat` (`QuickUnlockStore.cs:53-90`); DPAPI-wrapped again on Windows (`:98,111`), mode 0600 on macOS (`:120-125`).
- **Network:** favicons only (`UI/FaviconService.cs`), guarded by a test (VBR-16).

The ETH Zurich/USI analysis found 12 attacks on Bitwarden under a malicious server, most needing the client to send or re-encrypt something ([Scarlata et al., USENIX Security '26](https://eprint.iacr.org/2026/058)). Vaultbar sends nothing, but still shows whatever was synced (I2).

## 2. What Windows Hello and Touch ID gate

Both are real cryptographic gates.

- **Windows Hello** (`Platform/WindowsHello.cs:42-90`): a `KeyCredentialManager` key named `vaultbar` signs a random 32-byte challenge; HKDF of the signature is the AES-GCM key wrapping the user key (`QuickUnlockStore.cs:89-90`). The private key is in the TPM; signing needs face, fingerprint or the Hello PIN. Same "sign path" as Bitwarden's desktop app ([windows.rs](https://github.com/bitwarden/clients/blob/main/apps/desktop/desktop_native/biometric/src/windows.rs)). Bitwarden's 2023 design kept the key readable in Credential Manager ([CVE-2023-27706](https://blog.redteam-pentesting.de/2024/bitwarden-heist/)).
- **Touch ID** (`src/Vaultbar.TouchId/TouchId.swift:24-68`): a Secure Enclave P-256 key with `.privateKeyUsage` + `.biometryCurrentSet` and `WhenUnlockedThisDeviceOnly`; ECDH with a public key derived from the challenge yields the wrapping secret. A fresh touch every time, no passcode fallback, voided when enrolled fingers change. Stronger than Bitwarden's macOS app, which keeps the user key in the login keychain behind a yes/no `promptTouchID` ([os-biometrics-mac.service.ts](https://github.com/bitwarden/clients/blob/main/apps/desktop/src/key-management/biometrics/os-biometrics-mac.service.ts)).
- **Not bound:** which program asks (M3), and how long the stored key stays valid (M2).

## 3. Cached items, swap, dumps, logs

- **Decrypted for search while unlocked:** names, usernames, URIs, folders, card brand and last four (`VaultItem.cs:17-64`).
- **Encrypted until used:** passwords, TOTP secrets, notes, card numbers and codes, SSH private keys, hidden fields (`VaultItem.cs:105-148,211`). Lock drops every item and revealed field (`QuickWindow.axaml.cs:133-142`).
- **On disk:** no decrypted vault data; `usage.json` holds item IDs and timestamps only; favicons are memory-only.
- **Swap, pagefile, crash dumps:** key buffers are neither pinned nor locked (L1); a full dump or hibernation file taken while unlocked contains the user key, as with Bitwarden ([white paper](https://bitwarden.com/help/bitwarden-security-white-paper/)).
- **Logs:** exception type and HRESULT only (`Log.Shape`), item/folder/org GUIDs on decrypt failures (`VaultService.cs:393,422,466`), a stack trace on fatal errors (`Program.cs:63`). No secrets.

## 4. Clipboard and auto-type

- **Windows:** `CanIncludeInClipboardHistory=0`, `CanUploadToCloudClipboard=0`, `ExcludeClipboardContentFromMonitorProcessing` (`WindowsPlatform.cs:184-205`), as [Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats).
- **macOS:** `currentHostOnly` keeps copies off Universal Clipboard, plus the concealed and transient markers (`MacPlatform.cs:152-172`).
- **Clearing:** after 30 s by default and on lock, quit, logout and crash, only if unchanged (`AppController.cs:44,139-168`). Bitwarden's default is never.
- **Residual:** any app can read the clipboard meanwhile; a hard kill skips the clearing.
- **Auto-type:** checks focus before every keystroke (`AutoTyper.cs:11-39`) and refuses shell windows. Risks: H1 and L2.

## 5. IPC

One channel: the CLI link and the login item (LaunchAgent / HKCU Run, `Setup.cs:19-48`) are the same executable forwarding one word (show/toggle/lock/quit/ping; `AppController.cs:98-115`). No secrets cross it.

- **Windows:** a named pipe with `CurrentUserOnly` on both ends (`Ipc.cs:23,52-53`); other users cannot connect but can squat the name (L5).
- **macOS:** a socket in a 0700 folder (D-8).
- **Same user:** can toggle or lock the bar, which is harmless.

## 6. Update and install chain

- **CI:** Actions pinned by SHA, read-only token, `persist-credentials: false`, locked restore, Apple secrets only on tags (`release.yml:38-46`), provenance attestation (`:161-167`).
- **Not done yet:** no Developer ID, notarisation or hardened runtime (D-6, VBR-8); no Authenticode.
- **Installers:** check GitHub's SHA-256 of the asset and fail closed (`install-macos.sh:56-76`, `install.ps1:65-81`); that is integrity, not origin, since the digest comes from the same release. They then strip quarantine and re-sign ad hoc (`install-macos.sh:84-86`) or run `Unblock-File` (`install.ps1:87`). See H2.
- **The CI installer test:** the design is fine; the optional `GITHUB_TOKEN` sent only to the API through a header file (D-12) does not weaken it.
- **Updates:** no auto-update, by design. See L6.

## 7. Dependencies and comparison

`dotnet list package --vulnerable --include-transitive` (2026-11-14): no vulnerable packages. The only Dependabot alert (System.Formats.Cbor, GHSA-vmv3-wvjp-wqpj) is fixed in 10.0.15.

| | Vaultbar | Bitwarden desktop | Bitwarden browser extension |
| --- | --- | --- | --- |
| Talks to server | Never | Yes | Yes |
| Biometric key, Windows | TPM Hello signature, persists across restarts | Same sign path; master password/PIN after restart by default ([help](https://bitwarden.com/help/biometrics/)) | Through the desktop app |
| Biometric key, macOS | Secure Enclave, touch per use, not app-bound (M3) | Keychain item + yes/no Touch ID prompt, app-bound by keychain ACL | Through the desktop app |
| Which site gets the credential | Window title, user picks (H1) | No autofill outside the browser | URL matching |
| Clipboard default | Cleared after 30 s, out of history and cloud | Never cleared | Never cleared |
| Signed releases | Not yet (H2) | Yes | Store-signed |

## Findings and fixes

### H1 High: suggestions and auto-type trust the window title
`Matcher.cs:83-110` scores an item when one of its host labels appears in the foreground window title; `QuickWindow.axaml.cs:247` lists those under "SUGGESTED"; Enter runs `FillAsync` (`:411-415, 502-507, 594-650`). The title comes from `GetWindowTextW` (`WindowsPlatform.cs:76-78`) or the AX window title (`MacPlatform.cs:70`). Nothing compares the item's URIs with the page receiving the password, so a page titled "PayPal: Log in" at a look-alike host gets the real PayPal login offered first.
**Fix:** read the browser address (UI Automation on Windows; `AXURL` or browser scripting on macOS) and match with Bitwarden's URI rules; never suggest from a browser title alone; ask for a second Enter showing the host when the address is unknown or does not match; keep title matching for non-browser apps; tests for a look-alike title and the confirmation.

### H2 High: unsigned releases, and the installers strip the OS checks
`package.sh:39-43` signs ad hoc without `--options runtime`; `install-macos.sh:84-86` removes quarantine and re-signs; no Authenticode; `install.ps1:87` runs `Unblock-File`; the installers never check the attestation (`release.yml:161-167`).
**Fix:** finish VBR-8 and Windows signing; the installers verify the signer and refuse otherwise (`codesign --verify --strict` + `spctl -a -vv` with the Team ID pinned; `Get-AuthenticodeSignature` Valid with the expected subject); stop stripping quarantine; until then run `gh attestation verify` when `gh` is present and print "unsigned build" otherwise.

### M1 Medium: unprotected `main` and `v*` tags
No branch protection, rulesets or environments; the one-liners run `main`; a `v*` tag push runs the release job with the signing secrets.
**Fix:** a ruleset on `main` (PR plus the `build` checks, no force push or deletion), a tag ruleset for `v*`, and a `release` environment for the signing credentials. Repository settings: the owner's call.

### M2 Medium: quick unlock has no lifetime
`VaultService.cs:118-200` opens the vault from the stored key at any time, including after a reboot; no "master password at start" setting, no maximum age; the key survives logging out of the desktop app.
**Fix:** per-process quick-unlock secret by default, persistence across restarts as an explicit opt-in recorded in decisions.md, a maximum age (e.g. 14 days), and deleting `quick-unlock.dat` when the account disappears from `data.json`.

### M3 Medium: the Touch ID blob and the Hello key work for any same-user process
The Touch ID key blob sits in `~/Library/Caches/vaultbar/touch-id-key.dat` (`MacPlatform.cs:73`); a local experiment showed a Secure Enclave key blob created by one ad-hoc-signed binary loads in another. The Hello key is named `vaultbar` per user (`WindowsHello.cs:11`).
**Fix:** on macOS, after a Developer ID, a permanent Secure Enclave key in the data-protection keychain under Vaultbar's access group, re-enrolling existing users; on Windows, document the residual and check whether an MSIX build scopes the key per package.

### M4 Medium: the idle lock misreads Bitwarden's timeout
`VaultService.cs:353-355` accepts only a positive integer; `0` ("Immediately"), `onIdle`, `onSleep`, `onLocked`, `onRestart`, `never` and the legacy `-1` to `-4` ([vault-timeout.type.ts](https://github.com/bitwarden/clients/blob/main/libs/common/src/key-management/vault-timeout/types/vault-timeout.type.ts)) all become "no idle lock"; the timer skips while the bar is visible (`AppController.cs:76`).
**Fix:** `0` locks when the bar hides; `onIdle`/`-4` lock on OS idle time; `onSleep`/`onLocked` use the system-lock hook; `never`, `onRestart` and missing values fall back to a default such as 15 minutes unless `lockAfterMinutes` says otherwise; count idle time while the bar is open; tests.

### L1–L7 Low
- **L1:** pin key buffers (`GC.AllocateUninitializedArray<byte>(n, pinned: true)`) and `VirtualLock`/`mlock` them; `WerAddExcludedApplication`; reword the README's "zeroes the keys".
- **L2:** document keyboard-hook exposure; replace the silent fallback copy (`QuickWindow.axaml.cs:638-642`) with a toast offering the copy key.
- **L3:** a setting defaulting re-prompt to the master password only, or a decision record for the softer default.
- **L4:** a per-account Hello key name; `FailIfExists` then `OpenAsync`; check the raised window belongs to the system credential UI.
- **L5:** a random per-user pipe-name suffix stored in `%LOCALAPPDATA%\vaultbar`.
- **L6:** pin the SDK in `global.json`; a scheduled check of the shipped runtime against Microsoft's `releases-index.json`; optionally an "update available" line.
- **L7:** allow only Bitwarden's cloud icon hosts or the configured self-hosted base host for favicons.

### Info
I1 the offline integration model is the safest available; I2 crypto matches the official clients (constant-time MAC check `EncString.cs:20-33`, COSE v2 checks, KDF bounds `DesktopStore.cs:138-151`); I3 clipboard handling is stricter than Bitwarden's; I4 IPC carries no secrets and is user-scoped; I5 no secrets in logs; I6 no vulnerable dependencies, Actions pinned by SHA, locked restore.

## Sources
- https://bitwarden.com/help/bitwarden-security-white-paper/
- https://bitwarden.com/help/cli/
- https://bitwarden.com/help/biometrics/
- https://github.com/bitwarden/clients/blob/main/apps/desktop/desktop_native/biometric/src/windows.rs
- https://github.com/bitwarden/clients/blob/main/apps/desktop/src/key-management/biometrics/os-biometrics-mac.service.ts
- https://github.com/bitwarden/clients/blob/main/libs/common/src/key-management/vault-timeout/types/vault-timeout.type.ts
- https://blog.redteam-pentesting.de/2024/bitwarden-heist/ (CVE-2023-27706)
- https://eprint.iacr.org/2026/058 (Scarlata, Torrisi, Backendal, Paterson, USENIX Security '26)
- https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats
