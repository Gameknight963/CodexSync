#!/usr/bin/env bash
set -euo pipefail

api="https://api.github.com/repos/gameknight963/codexsync/releases"
prerelease=false
version=""
install_dir="${HOME}/.local/bin"
skill_dir="${HOME}/.agents/skills/codexsync-setup"
configure_path=true

fail() { printf 'CodexSync installation failed: %s\n' "$*" >&2; exit 1; }
usage() {
    printf '%s\n' 'Usage: install.sh [--prerelease] [--version TAG] [--install-dir DIR] [--skill-dir DIR] [--no-path]'
}
while [ "$#" -gt 0 ]; do
    case "$1" in
        --prerelease) prerelease=true; shift ;;
        --version|--install-dir|--skill-dir)
            [ "$#" -ge 2 ] && [ -n "$2" ] || fail "$1 requires a value."
            case "$1" in
                --version) version="$2" ;;
                --install-dir) install_dir="$2" ;;
                --skill-dir) skill_dir="$2" ;;
            esac
            shift 2 ;;
        --no-path) configure_path=false; shift ;;
        --help|-h) usage; exit 0 ;;
        *) usage >&2; fail "Unknown argument: $1" ;;
    esac
done

[ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] || fail 'This installer currently supports Linux x64 only.'
for dependency in curl jq; do
    command -v "$dependency" >/dev/null 2>&1 || fail "Install $dependency first using your distribution's package manager."
done
[[ "$install_dir" = /* && "$skill_dir" = /* ]] || fail 'Install and skill directories must be absolute paths.'
[ "$install_dir" != "$skill_dir" ] || fail 'Executable and skill directories must be separate.'

fetch() {
    curl --fail --show-error --silent --location --retry 2 --connect-timeout 15 \
        --proto '=https' --proto-redir '=https' \
        -H 'Accept: application/vnd.github+json' -H 'User-Agent: CodexSync-Installer' "$@"
}
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT

if [ -n "$version" ]; then
    encoded_version=$(jq -nr --arg value "$version" '$value | @uri')
    fetch "$api/tags/$encoded_version" -o "$temporary/release.json"
else
    page=1
    while :; do
        fetch "$api?per_page=100&page=$page" -o "$temporary/releases.json"
        jq --argjson prerelease "$prerelease" \
            '[.[] | select(.draft == false and ($prerelease or .prerelease == false))][0] // empty' \
            "$temporary/releases.json" > "$temporary/release.json"
        [ ! -s "$temporary/release.json" ] || break
        [ "$(jq 'length' "$temporary/releases.json")" -eq 100 ] || \
            fail 'No matching release found. If only prereleases exist, rerun with --prerelease or --version <tag>.'
        page=$((page + 1))
    done
fi
jq -e '.draft == false' "$temporary/release.json" >/dev/null || fail 'A draft release cannot be installed.'
tag=$(jq -er '.tag_name' "$temporary/release.json")
binary_url=$(jq -r '[.assets[] | select(.name == "codexsync-selfcontained")][0].browser_download_url // empty' "$temporary/release.json")
skill_url=$(jq -r '[.assets[] | select(.name == "SKILL.md")][0].browser_download_url // empty' "$temporary/release.json")
[ -n "$binary_url" ] && [ -n "$skill_url" ] || \
    fail "Release $tag is missing required assets. It may still be building; try again after the Release assets workflow finishes."

printf 'Downloading CodexSync %s...\n' "$tag"
fetch "$binary_url" -o "$temporary/codexsync"
fetch "$skill_url" -o "$temporary/SKILL.md"
[ -s "$temporary/codexsync" ] && [ -s "$temporary/SKILL.md" ] || fail 'A downloaded asset is empty.'
chmod +x "$temporary/codexsync"
"$temporary/codexsync" --help >/dev/null || fail 'The downloaded executable failed its startup check.'
mkdir -p -- "$install_dir" "$skill_dir"
install -m 755 "$temporary/codexsync" "$install_dir/codexsync"
install -m 644 "$temporary/SKILL.md" "$skill_dir/SKILL.md"

if [ "$configure_path" = true ]; then
    quoted_dir=$(jq -nr --arg value "$install_dir" '$value | @sh')
    path_line="export PATH=$quoted_dir:\"\$PATH\""
    profiles=("$HOME/.profile")
    case "${SHELL:-}" in
        */zsh) profiles+=("$HOME/.zshrc" "${ZDOTDIR:-$HOME}/.zprofile") ;;
        */bash)
            profiles+=("$HOME/.bashrc")
            if [ -f "$HOME/.bash_profile" ]; then profiles+=("$HOME/.bash_profile")
            elif [ -f "$HOME/.bash_login" ]; then profiles+=("$HOME/.bash_login"); fi ;;
    esac
    for profile in "${profiles[@]}"; do
        if [ ! -f "$profile" ] || ! grep -Fqx -- "$path_line" "$profile"; then
            printf '\n# CodexSync\n%s\n' "$path_line" >> "$profile"
        fi
    done
    printf 'PATH configured. Open a new terminal, or run: %s\n' "$path_line"
fi
printf 'Installed CodexSync %s to %s\nInstalled setup skill to %s\n' "$tag" "$install_dir" "$skill_dir"
printf 'Restart Codex to load the skill, then ask it to configure CodexSync. Existing configuration is preserved.\n'
