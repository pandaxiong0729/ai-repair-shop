#!/bin/sh
# Install the native Codex skill for the current user from a pinned release.
set -eu

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

case "$#" in
    0) ;;
    1) [ "$1" = "--start" ] || fail "Usage: sh install-desktop.sh [--start]" ;;
    *) fail "Usage: sh install-desktop.sh [--start]" ;;
esac

system=$(uname -s)
case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64|amd64) arch=x64 ;;
    *) fail "Unsupported processor: $(uname -m)" ;;
esac
case "$system" in
    Darwin) asset="AI-Repair-Shop-macOS-$arch.zip" ;;
    Linux) asset="AI-Repair-Shop-Linux-$arch.tar.gz" ;;
    *) fail "This installer supports macOS and Linux." ;;
esac

base='https://github.com/pandaxiong0729/ai-repair-shop/releases/download/v0.8.0'
tmp=$(mktemp -d "${TMPDIR:-/tmp}/ai-repair-shop.XXXXXXXX")
trap 'rm -rf "$tmp"' EXIT
trap 'exit 1' HUP INT TERM

fetch() {
    curl --fail --location --silent --show-error --retry 2 --proto '=https' --proto-redir '=https' \
        --output "$tmp/$1" "$base/$1"
}

printf 'Downloading %s\n' "$asset"
fetch SHA256SUMS.txt
expected=$(LC_ALL=C awk -v target="$asset" '
    { sub(/\r$/, "") }
    NF == 2 && ($2 == target || $2 == "*" target) {
        count++
        if (length($1) == 64 && $1 ~ /^[[:xdigit:]]+$/) digest = tolower($1)
    }
    END { if (count != 1 || digest == "") exit 1; print digest }
' "$tmp/SHA256SUMS.txt") || fail "Missing or ambiguous SHA-256 entry for $asset"
fetch "$asset"
if [ "$system" = Darwin ]; then
    actual=$(shasum -a 256 < "$tmp/$asset")
else
    actual=$(sha256sum < "$tmp/$asset")
fi
[ "${actual%% *}" = "$expected" ] || fail "SHA-256 mismatch: $asset"

mkdir "$tmp/app"
if [ "$system" = Darwin ]; then
    ditto -x -k "$tmp/$asset" "$tmp/app"
    program="$tmp/app/AI-Repair-Shop.app/Contents/MacOS/AI-Repair-Shop"
else
    tar -xzf "$tmp/$asset" -C "$tmp/app"
    program="$tmp/app/AI-Repair-Shop/AI-Repair-Shop"
fi
[ -x "$program" ] || fail "Missing executable in $asset"
"$program" --install-skill "$@"
printf '%s\n' 'Installed the AI Repair Shop Codex skill for the current user.'
