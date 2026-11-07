#!/bin/zsh
# Drives the real macOS backend in-process with a synthetic vault, in a throwaway HOME so it can never reach a real
# Bitwarden or Vaultbar folder. Needs a logged-in GUI session; needs no Accessibility permission and types nowhere else.
set -euo pipefail
here=${0:A:h}
dotnet build "$here/Vaultbar.MacE2E.csproj" -c Debug -m:2 --nologo -v q
"$here/../../src/Vaultbar.TouchId/build.sh" "$(uname -m)" "$here/bin/Debug/net10.0"
sandbox=$(mktemp -d)
trap 'rm -rf "$sandbox"' EXIT
out=${E2E_OUT:-$sandbox/shots}
mkdir -p "$out"
env HOME="$sandbox" VAULTBAR_CONFIG_DIR="$sandbox/cfg" E2E_SANDBOX="$sandbox" E2E_OUT="$out" \
  "$here/bin/Debug/net10.0/Vaultbar.MacE2E"
