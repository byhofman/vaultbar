#!/usr/bin/env zsh
# Installs (or removes) Vaultbar on macOS. No prompts or admin rights; uses ~/Applications if /Applications is not writable.
#   curl -fsSL https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install-macos.sh | zsh
#   curl -fsSL https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install-macos.sh | zsh -s -- --uninstall
# Options: --uninstall  --version vX.Y.Z  --archive path.tar.gz  --repo owner/name  --no-start  --dir path
set -euo pipefail

repo="byhofman/vaultbar" version="latest" archive="" uninstall=0 start=1 apps=""
while (( $# > 0 )); do
  case "$1" in
    --uninstall) uninstall=1 ;;
    --version) version="$2"; shift ;;
    --archive) archive="$2"; shift ;;
    --repo) repo="$2"; shift ;;
    --no-start) start=0 ;;
    --dir) apps="$2"; shift ;;
    *) print -u2 "unknown option: $1"; exit 2 ;;
  esac
  shift
done

if [[ -z $apps ]]; then
  apps=/Applications
  [[ -w $apps ]] || apps="$HOME/Applications"
fi
app="$apps/Vaultbar.app"
exe="$app/Contents/MacOS/vaultbar"
link="$HOME/.local/bin/vaultbar"

stop_running() {
  [[ -x $exe ]] && "$exe" quit >/dev/null 2>&1 || true
  pkill -x vaultbar >/dev/null 2>&1 || true
}

if (( uninstall )); then
  stop_running
  [[ -x $exe ]] && "$exe" autostart off >/dev/null 2>&1 || true
  rm -rf "$app" "$link"
  print "Vaultbar removed. Settings remain in ~/Library/Application Support/vaultbar"
  exit 0
fi

case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) print -u2 "unsupported architecture: $(uname -m)"; exit 1 ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
if [[ -z $archive ]]; then
  if [[ $version == latest ]]; then base="https://github.com/$repo/releases/latest/download"
  else base="https://github.com/$repo/releases/download/$version"; fi
  archive="$tmp/vaultbar-$rid.tar.gz"
  print "Downloading $base/vaultbar-$rid.tar.gz"
  curl -fsSL "$base/vaultbar-$rid.tar.gz" -o "$archive"
fi
tar -xzf "$archive" -C "$tmp"

stop_running
mkdir -p "$apps"
rm -rf "$app"
mv "$tmp/Vaultbar.app" "$app"
chmod +x "$exe"
xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
# A Developer ID signature is kept: replacing it would undo the notarization and the Accessibility grant.
codesign -dv "$app" 2>&1 | grep -q '^Authority=Developer ID' || codesign --force --deep --sign - "$app" >/dev/null 2>&1

mkdir -p "${link:h}"
ln -sf "$exe" "$link"
"$exe" autostart on >/dev/null
(( start )) && open "$app" --args background

[[ ":$PATH:" == *":${link:h}:"* ]] || print "note: ${link:h} is not on your PATH"
print "Vaultbar installed to $app. Press Cmd+Shift+Space."
print "For auto-type, allow Vaultbar in System Settings > Privacy & Security > Accessibility (it will ask)."
