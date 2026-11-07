#!/bin/zsh
# Mounts a built dmg read-only in a throwaway folder and checks what a person dragging it to Applications would get.
# Usage: tests/dmg-test-macos.sh dist/vaultbar-osx-arm64.dmg
set -uo pipefail
image=${1:A}
mount=$(mktemp -d)
failures=0

check() {
  local name=$1; shift
  if "$@" >/dev/null 2>&1; then print "PASS  $name"; else print "FAIL  $name"; failures=$((failures + 1)); fi
}
trap 'hdiutil detach "$mount" -force >/dev/null 2>&1; rmdir "$mount" 2>/dev/null' EXIT

check "image verifies" hdiutil verify "$image"
check "image mounts" hdiutil attach "$image" -nobrowse -readonly -mountpoint "$mount"
app=$mount/Vaultbar.app
check "has the app" test -d "$app"
check "has an Applications link" test -L "$mount/Applications"
check "app signature verifies" codesign --verify --deep --strict "$app"
check "app has the Touch ID library" test -f "$app/Contents/MacOS/libvaultbar-touchid.dylib"
check "Info.plist is valid" plutil -lint "$app/Contents/Info.plist"
check "bundle identifier is ours" zsh -c "plutil -extract CFBundleIdentifier raw '$app/Contents/Info.plist' | grep -qx dev.byhofman.vaultbar"

print "$failures failed"
exit $((failures > 0))
