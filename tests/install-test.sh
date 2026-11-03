#!/bin/bash
# Runs in ubuntu:24.04 with the repo at /w: exercises install/install.sh end to end and syntax-checks the macOS script.
set -u
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends zsh libice6 libsm6 libfontconfig1 libxtst6 libx11-6 >/dev/null 2>&1
fail=0
ok() { echo "PASS $1"; }
ko() { echo "FAIL $1"; fail=1; }

bash -n /w/install/install.sh && ok "install.sh syntax" || ko "install.sh syntax"
zsh -n /w/install/install-macos.sh && ok "install-macos.sh syntax" || ko "install-macos.sh syntax"

export HOME=/tmp/home && mkdir -p $HOME
out=$(bash /w/install/install.sh --archive /w/dist/vaultbar-linux-x64.tar.gz --no-start 2>&1); echo "$out"
echo "$out" | grep -q "missing system libraries" && ko "no false library warning" || ok "no false library warning"
[ -x $HOME/.local/bin/vaultbar ] && ok "binary installed" || ko "binary installed"
[ "$($HOME/.local/bin/vaultbar version)" = "0.1.0" ] && ok "binary runs" || ko "binary runs"
grep -q "Exec=\"$HOME/.local/bin/vaultbar\" background" $HOME/.config/autostart/vaultbar.desktop && ok "autostart entry" || ko "autostart entry"

bash /w/install/install.sh --archive /w/dist/vaultbar-linux-x64.tar.gz --no-start >/dev/null && ok "reinstall (update)" || ko "reinstall (update)"

bash /w/install/install.sh --uninstall
[ ! -e $HOME/.local/bin/vaultbar ] && ok "binary removed" || ko "binary removed"
[ ! -e $HOME/.config/autostart/vaultbar.desktop ] && ok "autostart removed" || ko "autostart removed"
exit $fail
