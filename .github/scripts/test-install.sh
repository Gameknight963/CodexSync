#!/usr/bin/env bash
set -euo pipefail

fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
export CODEXSYNC_INSTALLER_FIXTURE="$fixture"
mkdir -p "$fixture/network" "$fixture/bin"
cat > "$fixture/bin/curl" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
output=""
url=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) output="$2"; shift 2 ;;
        -H|--retry|--connect-timeout|--proto|--proto-redir) shift 2 ;;
        https://*) url="$1"; shift ;;
        *) shift ;;
    esac
done
case "$url" in
    */tags/*) source=release.json ;;
    *releases\?*) source=releases.json ;;
    https://fixture/binary) source=codexsync ;;
    https://fixture/skill) source=SKILL.md ;;
    *) exit 1 ;;
esac
cp "$CODEXSYNC_INSTALLER_FIXTURE/network/$source" "$output"
MOCK
chmod +x "$fixture/bin/curl"
export PATH="$fixture/bin:$PATH"

# Use the actual published Linux executable to check installation and startup.
cp "$1" "$fixture/network/codexsync"
cp .agents/skills/codexsync-setup/SKILL.md "$fixture/network/SKILL.md"
cat > "$fixture/network/release.json" <<'JSON'
{"tag_name":"v-test","draft":false,"prerelease":true,"assets":[{"name":"codexsync-selfcontained","browser_download_url":"https://fixture/binary"},{"name":"SKILL.md","browser_download_url":"https://fixture/skill"}]}
JSON
jq -s '.' "$fixture/network/release.json" > "$fixture/network/releases.json"
arguments=(--no-path --install-dir "$fixture/installed" --skill-dir "$fixture/skill")
if bash install.sh "${arguments[@]}" > "$fixture/error" 2>&1; then
    echo 'Expected stable selection to reject a prerelease.' >&2; exit 1
fi
grep -q 'No matching release' "$fixture/error"
test ! -d "$fixture/installed"
bash install.sh "${arguments[@]}" --prerelease
test -x "$fixture/installed/codexsync"
cmp "$fixture/skill/SKILL.md" "$fixture/network/SKILL.md"
printf 'existing configuration\n' > "$fixture/installed/preserve.txt"
bash install.sh "${arguments[@]}" --version v-test
test -f "$fixture/installed/preserve.txt"
jq '.prerelease = false' "$fixture/network/release.json" > "$fixture/network/next.json"
mv "$fixture/network/next.json" "$fixture/network/release.json"
jq -s '.' "$fixture/network/release.json" > "$fixture/network/releases.json"
bash install.sh "${arguments[@]}"
printf '[{"tag_name":"v-test","draft":false,"prerelease":false,"assets":[]}]\n' > "$fixture/network/releases.json"
if bash install.sh "${arguments[@]}" > "$fixture/error" 2>&1; then
    echo 'Expected missing assets to be rejected.' >&2; exit 1
fi
grep -q 'may still be building' "$fixture/error"
printf 'Linux installer tests passed.\n'
