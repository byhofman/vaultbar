#!/bin/zsh
# Runs install-macos.sh against a built archive, in a throwaway HOME and install folder.
# Usage: tests/install-test-macos.sh dist/vaultbar-osx-arm64.tar.gz
set -uo pipefail
archive=${1:A}
script=${0:A:h:h}/install/install-macos.sh
sandbox=${$(mktemp -d):A}
apps=$sandbox/Apps
exe=$apps/Vaultbar.app/Contents/MacOS/vaultbar
export HOME=$sandbox VAULTBAR_CONFIG_DIR=$sandbox/cfg BITWARDEN_APPDATA_DIR=$sandbox/none
plist=$HOME/Library/LaunchAgents/vaultbar.plist
link=$HOME/.local/bin/vaultbar
failures=0

check() {
  local name=$1; shift
  if "$@" >/dev/null 2>&1; then print "PASS  $name"; else print "FAIL  $name"; failures=$((failures + 1)); fi
}
stop() { "$exe" quit >/dev/null 2>&1; sleep 1; }
trap 'stop; rm -rf "$sandbox"' EXIT

zsh "$script" --archive "$archive" --dir "$apps" --no-start >/dev/null
check "install succeeds" test -x "$exe"
check "bundle signature verifies" codesign --verify --deep --strict "$apps/Vaultbar.app"
check "no quarantine left" zsh -c "! xattr -lr '$apps/Vaultbar.app' | grep -q com.apple.quarantine"
check "Info.plist is valid" plutil -lint "$apps/Vaultbar.app/Contents/Info.plist"
check "CLI link runs" "$link" version
check "login item plist is valid" plutil -lint "$plist"
check "login item points at the app" zsh -c "plutil -extract ProgramArguments.0 raw '$plist' | grep -qx '$exe'"
check "login item starts in the background" zsh -c "plutil -extract ProgramArguments.1 raw '$plist' | grep -qx background"

mkdir -p "$VAULTBAR_CONFIG_DIR"
print '{ "dataPath": "'$sandbox'/none/data.json", "showFavicons": false }' > "$VAULTBAR_CONFIG_DIR/config.json"
"$link" background &!
for i in {1..40}; do "$link" lock >/dev/null 2>&1 && break; sleep 0.25; done
check "app starts and answers" "$link" lock

zsh "$script" --archive "$archive" --dir "$apps" --no-start >/dev/null
check "update stops the running app" zsh -c "! '$link' lock"
check "update leaves a working app" "$link" version

"$link" background &!
for i in {1..40}; do "$link" lock >/dev/null 2>&1 && break; sleep 0.25; done
zsh "$script" --uninstall --dir "$apps" >/dev/null
check "uninstall removes the app" test ! -e "$apps/Vaultbar.app"
check "uninstall removes the CLI link" test ! -L "$link"
check "uninstall removes the login item" test ! -e "$plist"
check "uninstall stops the app" zsh -c "! pgrep -x vaultbar"

print "$failures failed"
exit $((failures > 0))
