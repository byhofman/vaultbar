#!/usr/bin/env bash
# Installs (or removes) Vaultbar for the current user on Linux. No root, no prompts.
#   curl -fsSL https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install.sh | bash -s -- --uninstall
# Options: --uninstall  --version vX.Y.Z  --archive path.tar.gz  --repo owner/name  --no-start
set -euo pipefail

repo="byhofman/vaultbar" version="latest" archive="" uninstall=0 start=1
bin_dir="${XDG_BIN_HOME:-$HOME/.local/bin}"
while [ $# -gt 0 ]; do
  case "$1" in
    --uninstall) uninstall=1 ;;
    --version) version="$2"; shift ;;
    --archive) archive="$2"; shift ;;
    --repo) repo="$2"; shift ;;
    --no-start) start=0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
exe="$bin_dir/vaultbar"

stop_running() {
  if [ -x "$exe" ]; then "$exe" quit >/dev/null 2>&1 || true; fi
  pkill -x vaultbar >/dev/null 2>&1 || true
}

if [ "$uninstall" = 1 ]; then
  stop_running
  if [ -x "$exe" ]; then "$exe" autostart off >/dev/null 2>&1 || true; fi
  if command -v gsettings >/dev/null 2>&1; then
    path=/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/vaultbar/
    list=$(gsettings get org.gnome.settings-daemon.plugins.media-keys custom-keybindings 2>/dev/null || echo "")
    if [[ "$list" == *"$path"* ]]; then
      new=$(echo "$list" | sed "s|, '$path'||; s|'$path', ||; s|'$path'||")
      [ "$new" = "[]" ] && new="@as []"
      gsettings set org.gnome.settings-daemon.plugins.media-keys custom-keybindings "$new"
    fi
  fi
  rm -f "$exe"
  echo "Vaultbar removed. Settings remain in ${XDG_CONFIG_HOME:-$HOME/.config}/vaultbar"
  exit 0
fi

case "$(uname -m)" in
  x86_64 | amd64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
if [ -z "$archive" ]; then
  if [ "$version" = latest ]; then base="https://github.com/$repo/releases/latest/download"
  else base="https://github.com/$repo/releases/download/$version"; fi
  archive="$tmp/vaultbar-$rid.tar.gz"
  echo "Downloading $base/vaultbar-$rid.tar.gz"
  if command -v curl >/dev/null 2>&1; then curl -fsSL "$base/vaultbar-$rid.tar.gz" -o "$archive"
  else wget -qO "$archive" "$base/vaultbar-$rid.tar.gz"; fi
fi
tar -xzf "$archive" -C "$tmp"

stop_running
mkdir -p "$bin_dir"
install -m 755 "$tmp/vaultbar" "$exe"
"$exe" autostart on >/dev/null

missing=""
for lib in libX11.so.6 libXtst.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1; do
  ldconfig -p 2>/dev/null | grep -q "[[:space:]]$lib[[:space:]]" || missing="$missing $lib"
done
[ -n "$missing" ] && echo "warning: missing system libraries:$missing (install your distro's X11 client libraries, e.g. libice6 libsm6 libxtst6)" >&2

if [ "${XDG_SESSION_TYPE:-}" = wayland ] || [ -n "${WAYLAND_DISPLAY:-}" ]; then
  if command -v gsettings >/dev/null 2>&1 && [[ "${XDG_CURRENT_DESKTOP:-}" == *GNOME* ]]; then
    "$exe" shortcut
  else
    echo "Wayland: bind a keyboard shortcut to '$exe toggle' in your desktop settings."
  fi
fi

if [ "$start" = 1 ] && { [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; }; then
  nohup "$exe" background >/dev/null 2>&1 &
fi

case ":$PATH:" in *":$bin_dir:"*) ;; *) echo "note: $bin_dir is not on your PATH" ;; esac
echo "Vaultbar installed to $exe. Press Ctrl+Shift+Space."
