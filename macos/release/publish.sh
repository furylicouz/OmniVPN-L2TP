#!/bin/bash
# Публикация релиза OmniVPN Connect на GitHub: установщик Windows, образ macOS и omnivpn-update.txt,
# по которому программы находят обновление.
#   release/publish.sh 1.9.0 release/notes-1.9.0.txt
# Запускается из папки macos. Нужны: build/OmniVPNConnect-<версия>.dmg (./build.sh),
# release/OmniVPNConnect-Setup-<версия>.exe (windows\build.cmd) и вход в GitHub (см. TOKEN ниже).
set -euo pipefail
cd "$(dirname "$0")/.."

REPO="furylicouz/OmniVPN-L2TP"
API="https://api.github.com/repos/$REPO"
VERSION="${1:?версия, например 1.9.0}"
NOTES="${2:?файл с описанием: по пункту в строке}"
TAG="v$VERSION"
EXE="release/OmniVPNConnect-Setup-$VERSION.exe"
DMG="build/OmniVPNConnect-$VERSION.dmg"
MANIFEST="release/omnivpn-update.txt"

for f in "$EXE" "$DMG" "$NOTES"; do [ -s "$f" ] || { echo "Нет файла $f"; exit 1; }; done
PLIST_VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Info.plist)
[ "$PLIST_VERSION" = "$VERSION" ] || { echo "Info.plist: версия $PLIST_VERSION, а публикуем $VERSION"; exit 1; }

# Описание обновления для программ.
size() { stat -f %z "$1"; }
sha() { shasum -a 256 "$1" | cut -d' ' -f1; }
{
  echo "# OmniVPN Connect $VERSION — описание обновления для программ"
  echo "version = $VERSION"
  grep -v '^[[:space:]]*$' "$NOTES" | sed 's/^[-•*][[:space:]]*//; s/^/notes = /'
  echo "windows = $(basename "$EXE")"
  echo "windows.size = $(size "$EXE")"
  echo "windows.sha256 = $(sha "$EXE")"
  echo "mac = $(basename "$DMG")"
  echo "mac.size = $(size "$DMG")"
  echo "mac.sha256 = $(sha "$DMG")"
} > "$MANIFEST"

text=$(grep -v '^[[:space:]]*$' "$NOTES" | sed 's/^[-•*][[:space:]]*//; s/^/- /')
text="$text

**Windows:** \`$(basename "$EXE")\` · **macOS** (Apple Silicon и Intel): \`$(basename "$DMG")\`

Установленные программы обновляются сами: «О программе» → «Обновить»."

# DRY_RUN=1 — только показать, что будет опубликовано.
if [ "${DRY_RUN:-}" = 1 ]; then cat "$MANIFEST"; echo; echo "$text"; exit 0; fi

# Токен: GITHUB_TOKEN, файл ~/.config/omnivpn/github-token или вход tools/gh auth login --insecure-storage.
TOKEN="${GITHUB_TOKEN:-$(cat ~/.config/omnivpn/github-token 2>/dev/null || tools/gh auth token 2>/dev/null || true)}"
[ -n "$TOKEN" ] || { echo "Нет входа в GitHub: tools/gh auth login --web --insecure-storage"; exit 1; }

# Токен передаём curl через файл, а не в командной строке (её видно в списке процессов).
AUTH=$(mktemp)
trap 'rm -f "$AUTH" /tmp/omnivpn-gh.json' EXIT
chmod 600 "$AUTH"
printf 'Authorization: Bearer %s\n' "$TOKEN" > "$AUTH"

gh() {  # gh МЕТОД ПУТЬ [JSON] — запрос к API, ответ в /tmp/omnivpn-gh.json, код ответа в stdout
  local args=(-sS -o /tmp/omnivpn-gh.json -w "%{http_code}" -X "$1" -H @"$AUTH"
              -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28")
  if [ -n "${3:-}" ]; then args+=(-H "Content-Type: application/json" --data "$3"); fi
  curl "${args[@]}" "$API$2"
}
fail() { echo "$1: $(jq -r '.message // .' /tmp/omnivpn-gh.json 2>/dev/null)"; exit 1; }

code=$(gh GET "")
[ "$code" = 200 ] && [ "$(jq -r .permissions.push /tmp/omnivpn-gh.json)" = true ] || fail "Нет прав на запись в $REPO (код $code)"

[ "$(gh GET "/releases/tags/$TAG")" = 404 ] || { echo "Релиз $TAG уже есть — поднимите версию"; exit 1; }

# Сначала черновик: программы не увидят релиз, пока в нём нет всех файлов.

body=$(jq -n --arg t "$TAG" --arg n "OmniVPN Connect $VERSION" --arg b "$text" \
  '{tag_name: $t, target_commitish: "main", name: $n, body: $b, draft: true}')
[ "$(gh POST /releases "$body")" = 201 ] || fail "Не удалось создать релиз"
ID=$(jq -r .id /tmp/omnivpn-gh.json)

for f in "$EXE" "$DMG" "$MANIFEST"; do
  name=$(basename "$f")
  code=$(curl -sS -o /tmp/omnivpn-gh.json -w "%{http_code}" -X POST -H @"$AUTH" \
    -H "Content-Type: application/octet-stream" --data-binary @"$f" \
    "https://uploads.github.com/repos/$REPO/releases/$ID/assets?name=$name")
  [ "$code" = 201 ] || { gh DELETE "/releases/$ID" >/dev/null; fail "Не удалось загрузить $name, черновик удалён"; }
  echo "загружен $name ($(size "$f") байт)"
done

[ "$(gh PATCH "/releases/$ID" '{"draft": false, "make_latest": "true"}')" = 200 ] || fail "Не удалось опубликовать релиз"
echo "Опубликован: $(jq -r .html_url /tmp/omnivpn-gh.json)"

# Так программы будут проверять обновление.
sleep 3
latest=$(curl -sSL "https://github.com/$REPO/releases/latest/download/omnivpn-update.txt" | sed -n 's/^version = //p')
echo "Последний релиз для программ: $latest"
[ "$latest" = "$VERSION" ]
