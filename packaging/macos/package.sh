#!/bin/zsh
# Turns dist/vaultbar-<rid>.tar.gz into dist/vaultbar-<rid>.dmg.
# With the APPLE_* variables set it signs with Developer ID, notarizes and staples both the app and the dmg, and rewrites
# the tar.gz so the one-liner gets the signed app too. Without them it signs ad hoc, and a browser download needs
# Open Anyway once.
# Usage: package.sh <dist folder> <rid>...
set -euo pipefail
dist=${1:A}; shift
here=${0:A:h}
work=$(mktemp -d)
keychain=""
cleanup() {
  [[ -n $keychain ]] && security delete-keychain "$keychain" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

signed=0
if [[ -n ${APPLE_CERTIFICATE_P12:-} ]]; then
  : "${APPLE_CERTIFICATE_PASSWORD:?} ${APPLE_SIGNING_IDENTITY:?} ${APPLE_ID:?} ${APPLE_TEAM_ID:?} ${APPLE_APP_PASSWORD:?}"
  signed=1
  # A throwaway keychain, so the certificate never lands in a login keychain.
  keychain=$work/signing.keychain-db
  keychain_password=$(uuidgen)
  security create-keychain -p "$keychain_password" "$keychain"
  security set-keychain-settings -lut 3600 "$keychain"
  security unlock-keychain -p "$keychain_password" "$keychain"
  print -r -- "$APPLE_CERTIFICATE_P12" | base64 --decode > "$work/cert.p12"
  security import "$work/cert.p12" -k "$keychain" -P "$APPLE_CERTIFICATE_PASSWORD" -T /usr/bin/codesign >/dev/null
  rm -f "$work/cert.p12"
  security set-key-partition-list -S apple-tool:,apple: -s -k "$keychain_password" "$keychain" >/dev/null
  security list-keychains -d user -s "$keychain" ${(f)"$(security list-keychains -d user | tr -d '" ')"}
fi

notarize() {
  xcrun notarytool submit "$1" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait --timeout 30m
}

sign_app() {
  local app=$1
  if (( ! signed )); then
    codesign --force --deep --sign - "$app"
    return
  fi
  # Inside out, and without --deep: notarization wants every nested file signed with the hardened runtime.
  local file
  for file in "$app"/Contents/MacOS/*.dylib(N); do
    codesign --force --timestamp --options runtime --sign "$APPLE_SIGNING_IDENTITY" "$file"
  done
  codesign --force --timestamp --options runtime --entitlements "$here/Vaultbar.entitlements" --sign "$APPLE_SIGNING_IDENTITY" "$app"
}

for rid in "$@"; do
  stage=$work/$rid
  mkdir -p "$stage/dmg"
  tar -xzf "$dist/vaultbar-$rid.tar.gz" -C "$stage/dmg"
  app=$stage/dmg/Vaultbar.app
  sign_app "$app"
  codesign --verify --deep --strict "$app"

  if (( signed )); then
    ditto -c -k --keepParent "$app" "$stage/submit.zip"
    notarize "$stage/submit.zip"
    xcrun stapler staple "$app"
    tar -czf "$dist/vaultbar-$rid.tar.gz" -C "$stage/dmg" Vaultbar.app
  fi

  ln -s /Applications "$stage/dmg/Applications"
  image=$dist/vaultbar-$rid.dmg
  # hdiutil fails intermittently on CI runners ("Resource busy"), so it gets a few attempts.
  for attempt in 1 2 3 4 5; do
    hdiutil create -volname Vaultbar -srcfolder "$stage/dmg" -format UDZO -ov "$image" >/dev/null && break
    (( attempt == 5 )) && exit 1
    sleep 5
  done

  if (( signed )); then
    codesign --force --timestamp --sign "$APPLE_SIGNING_IDENTITY" "$image"
    notarize "$image"
    xcrun stapler staple "$image"
    spctl --assess --type open --context context:primary-signature "$image"
  fi
done

(( signed )) || print "note: no APPLE_CERTIFICATE_P12, so these are ad-hoc signed"
