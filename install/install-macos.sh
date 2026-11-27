#!/usr/bin/env zsh
# Installs (or removes) Vaultbar on macOS. No prompts or admin rights; uses ~/Applications if /Applications is not writable.
#   curl -fsSL https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install-macos.sh | zsh
#   curl -fsSL https://raw.githubusercontent.com/byhofman/vaultbar/main/install/install-macos.sh | zsh -s -- --uninstall
# Options: --uninstall  --version vX.Y.Z  --archive path.tar.gz [--sha256 hex]  --repo owner/name  --no-start  --dir path
# A download is checked against the SHA-256 digest GitHub records for the release asset, and refused if it differs.
# Once releases are signed (team_id below), the app must carry that Developer ID and pass Gatekeeper, or nothing is
# installed. Until then the build is unsigned: with GitHub CLI signed in, its build attestation must verify instead.
# GITHUB_TOKEN, when set, authenticates the release lookup; anonymous lookups share a 60-an-hour limit per IP address.
set -euo pipefail

# The Apple Team ID that signs Vaultbar releases: empty until a Developer ID exists (#68). VAULTBAR_EXPECT_TEAM_ID
# only exists so the tests can switch the signed path on against an unsigned build.
team_id=""
[[ -n $team_id ]] || team_id=${VAULTBAR_EXPECT_TEAM_ID:-}
# The first release built with an attestation; older ones have none to check.
first_attested=0.4.0

repo="byhofman/vaultbar" version="latest" archive="" expected="" uninstall=0 start=1 apps=""
while (( $# > 0 )); do
  case "$1" in
    --uninstall) uninstall=1 ;;
    --version) version="$2"; shift ;;
    --archive) archive="$2"; shift ;;
    --sha256) expected="$2"; shift ;;
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
  asset="vaultbar-$rid.tar.gz"
  if [[ $version == latest ]]; then api="https://api.github.com/repos/$repo/releases/latest"
  else api="https://api.github.com/repos/$repo/releases/tags/$version"; fi
  auth=()
  # A header file keeps the token out of the process list.
  [[ -n ${GITHUB_TOKEN:-} ]] && { print -r -- "Authorization: Bearer $GITHUB_TOKEN" > "$tmp/auth"; auth=(-H "@$tmp/auth"); }
  curl -fsSL "${auth[@]}" "$api" -o "$tmp/release.json" || { print -u2 "Could not read the release from GitHub ($api). Try again later, or download it and use --archive."; exit 1; }
  tokens=$(grep -oE '"(tag_name|name|digest)": *"[^"]*"' "$tmp/release.json")
  tag=$(print -r -- "$tokens" | sed -n 's/^"tag_name": *"\(.*\)"$/\1/p' | head -n 1)
  expected=$(print -r -- "$tokens" | awk -v want="\"name\": \"$asset\"" 'found && /^"digest"/ { sub(/.*"sha256:/, ""); sub(/"$/, ""); print; exit } $0 == want { found = 1 }')
  [[ -n $tag && -n $expected ]] || { print -u2 "Release $version has no $asset with a SHA-256 digest."; exit 1; }
  release_tag=$tag
  archive="$tmp/$asset"
  print "Downloading $asset ($tag)"
  curl -fsSL "https://github.com/$repo/releases/download/$tag/$asset" -o "$archive"
fi
if [[ -n $expected ]]; then
  actual=$(shasum -a 256 "$archive" | cut -d" " -f1)
  if [[ $actual != "$expected" ]]; then
    print -u2 "SHA-256 mismatch for $archive: expected $expected, got $actual. Nothing was installed."
    exit 1
  fi
fi
tar -xzf "$archive" -C "$tmp"

older_than() { # older_than 0.3.1 0.4.0
  local a=(${(s:.:)1}) b=(${(s:.:)2}) i
  for i in 1 2 3; do (( ${a[i]:-0} < ${b[i]:-0} )) && return 0; (( ${a[i]:-0} > ${b[i]:-0} )) && return 1; done
  return 1
}

if [[ -n $team_id ]]; then
  # Signed releases: the Developer ID must be ours and Gatekeeper must accept the app, notarization included.
  refuse() { print -u2 "$1 Nothing was installed."; exit 1; }
  codesign --verify --deep --strict "$tmp/Vaultbar.app" 2>/dev/null || refuse "The app's signature does not verify."
  signer=$(codesign -dv --verbose=2 "$tmp/Vaultbar.app" 2>&1 | sed -n 's/^TeamIdentifier=//p')
  [[ $signer == "$team_id" ]] || refuse "The app is not signed by Vaultbar's Developer ID ($team_id; found ${signer:-none})."
  spctl --assess --type execute "$tmp/Vaultbar.app" 2>/dev/null || refuse "Gatekeeper does not accept the app."
  print "Signed by Developer ID $team_id and accepted by Gatekeeper."
else
  print "Unsigned build: macOS cannot check who made it."
  if [[ -n ${release_tag:-} ]] && older_than "${release_tag#v}" $first_attested; then
    print "$release_tag predates build attestations, so only its SHA-256 digest was checked."
  elif [[ -n ${release_tag:-} ]] && command -v gh >/dev/null && { [[ -n ${GH_TOKEN:-}${GITHUB_TOKEN:-} ]] || gh auth status >/dev/null 2>&1; }; then
    GH_TOKEN=${GH_TOKEN:-${GITHUB_TOKEN:-}} gh attestation verify "$archive" --repo "$repo" --signer-workflow "$repo/.github/workflows/release.yml" \
      --source-ref "refs/tags/$release_tag" --deny-self-hosted-runners >/dev/null 2>"$tmp/attestation.log" \
      || { cat "$tmp/attestation.log" >&2; print -u2 "The build attestation of $asset does not verify. Nothing was installed."; exit 1; }
    print "Its build attestation verifies: built by $repo's release workflow from $release_tag."
  elif [[ -n ${release_tag:-} ]]; then
    print "Install GitHub CLI and sign in (gh auth login) to have its build attestation checked too."
  else
    print "A local archive: check its build attestation yourself with gh attestation verify <file> --repo $repo."
  fi
fi

stop_running
mkdir -p "$apps"
rm -rf "$app"
mv "$tmp/Vaultbar.app" "$app"
chmod +x "$exe"
if [[ -z $team_id ]]; then
  # An unsigned build only opens without quarantine and with a local signature. A signed one keeps both, so Gatekeeper
  # still checks it at first launch.
  xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
  codesign --force --deep --sign - "$app" >/dev/null 2>&1
fi

mkdir -p "${link:h}"
ln -sf "$exe" "$link"
"$exe" autostart on >/dev/null
(( start )) && open "$app" --args background

[[ ":$PATH:" == *":${link:h}:"* ]] || print "note: ${link:h} is not on your PATH"
print "Vaultbar installed to $app. Press Cmd+Shift+Space."
print "For auto-type, allow Vaultbar in System Settings > Privacy & Security > Accessibility (it will ask)."
