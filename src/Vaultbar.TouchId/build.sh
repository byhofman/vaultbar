#!/bin/zsh
# Builds the Touch ID library next to the app. Usage: build.sh <arm64|x86_64> <output folder>
set -euo pipefail
swiftc -O -emit-library -module-name VaultbarTouchId -target "$1-apple-macos11" \
  -Xlinker -install_name -Xlinker @rpath/libvaultbar-touchid.dylib \
  "${0:A:h}/TouchId.swift" -o "$2/libvaultbar-touchid.dylib"
