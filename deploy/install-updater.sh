#!/usr/bin/env bash
# Install as the logged-in Docker Desktop user on the Mac Studio.
set -euo pipefail
umask 077
[[ $(uname -s) == Darwin && $(uname -m) == arm64 ]]
root=${HOSTING_DEPLOY_DIR:-"$HOME/csweet-hosting"}
[[ "$root" = /* && -r "$root/hosting.env" ]] || { echo 'Configure hosting.env first.' >&2; exit 1; }
export PATH="/usr/local/bin:/opt/homebrew/bin:$HOME/.docker/bin:$PATH"
command -v jq >/dev/null
docker info >/dev/null
source_dir=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$root/bin" "$HOME/Library/LaunchAgents"
install -m 0700 "$source_dir/pull-release.sh" "$root/bin/pull-release.sh"
label=com.csweet.hosting.updater
plist="$HOME/Library/LaunchAgents/$label.plist"
temp=$(mktemp "$root/launch-agent.XXXXXX")
trap 'rm -f "$temp"' EXIT
plutil -create xml1 "$temp"
plutil -insert Label -string "$label" "$temp"
plutil -insert ProgramArguments -json '[]' "$temp"
plutil -insert ProgramArguments.0 -string /bin/bash "$temp"
plutil -insert ProgramArguments.1 -string "$root/bin/pull-release.sh" "$temp"
plutil -insert EnvironmentVariables -json '{}' "$temp"
plutil -insert EnvironmentVariables.HOSTING_DEPLOY_DIR -string "$root" "$temp"
plutil -insert StartInterval -integer 180 "$temp"
plutil -insert RunAtLoad -bool YES "$temp"
plutil -insert StandardErrorPath -string "$root/updater.log" "$temp"
plutil -insert StandardOutPath -string "$root/updater.log" "$temp"
plutil -lint "$temp"
domain="gui/$(id -u)"
if launchctl print "$domain/$label" >/dev/null 2>&1; then launchctl bootout "$domain/$label"; fi
install -m 0600 "$temp" "$plist"
launchctl bootstrap "$domain" "$plist"
echo "Updater installed. Check $root/updater.log and the authenticated API health check."
