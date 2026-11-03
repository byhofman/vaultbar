#!/bin/bash
# Runs inside ubuntu:24.04 with the repo mounted at /w. Drives vaultbar on a bare X server:
# global hotkey (XGrabKey), unlock, search, auto-type (XTest, incl. non-layout characters), copy, CLI.
set -u
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends \
  xvfb libice6 libsm6 libfontconfig1 libxtst6 libx11-6 python3 fonts-dejavu-core >/dev/null 2>&1
H=/w/tests/e2e-linux
OUT=/w/dist/e2e
mkdir -p $OUT
fail=0
check() { if grep -qF -- "$2" /tmp/typed.txt; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi; }

Xvfb :77 -screen 0 1600x1000x24 > /tmp/xvfb.log 2>&1 &
sleep 2
export DISPLAY=:77
python3 $H/xtarget.py /tmp/typed.txt &
sleep 1

mkdir -p /tmp/demo && cp /w/dist/e2e-data.json /tmp/demo/data.json
printf '{"hotkey":"Ctrl+Shift+F11","dataPath":"/tmp/demo/data.json","showFavicons":false}' > /tmp/demo/config.json
cp /w/dist/linux-x64/vaultbar /tmp/vaultbar && chmod +x /tmp/vaultbar
export VAULTBAR_CONFIG_DIR=/tmp/demo
setsid /tmp/vaultbar background > /tmp/out.log 2>&1 < /dev/null &
for i in $(seq 1 40); do /tmp/vaultbar lock 2>/dev/null && break; sleep 1; done

python3 $H/xdriver.py key:ctrl+shift+F11 wait:25 sleep:0.5 shot:$OUT/01-locked.png
python3 $H/xdriver.py type:demo-password key:Return sleep:2 shot:$OUT/02-unlocked.png
python3 $H/xdriver.py type:github sleep:0.8 shot:$OUT/03-search.png key:Return sleep:4
check "autotype ascii" "alex-example<TAB>gh-Secr3t!pass"

python3 $H/xdriver.py key:ctrl+shift+F11 wait:10 sleep:0.3 type:docker sleep:0.6 key:ctrl+c sleep:0.3 shot:$OUT/04-copied.png
sleep 1
python3 $H/xdriver.py activate:"Login - Example App" sleep:0.5 key:ctrl+shift+F11 wait:10 sleep:0.3 type:caf sleep:0.5 key:Return sleep:4
check "autotype unicode" "gäst<TAB>pässwörd€ñ"

/tmp/vaultbar lock && echo "PASS cli lock" || { echo "FAIL cli lock"; fail=1; }
/tmp/vaultbar quit && sleep 1
pgrep -x vaultbar >/dev/null && { echo "FAIL cli quit"; fail=1; } || echo "PASS cli quit"
[ -s /tmp/demo/cache/vaultbar.log ] && { echo "--- app log"; cat /tmp/demo/cache/vaultbar.log; }
exit $fail
