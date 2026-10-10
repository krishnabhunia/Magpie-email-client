#!/usr/bin/env bash
# Builds Magpie for Mac (Apple Silicon) and writes macOS/out/Magpie_<version>.dmg.
#
#   bash macOS/build/build.sh <version>        e.g. 8.0.0 or 8.0.0-beta.95 (CI passes the version it works out)
#
# Needs the .NET 10 SDK (macOS/global.json) and Python 3. The .app, its icon, the ad-hoc signature and the disk
# image need macOS (sips, iconutil, codesign, hdiutil); on Linux the script stops after assembling Magpie.app and
# says so, which is enough to check the build. Fails loudly on any error.
set -euo pipefail

VERSION="${1:-}"
if [ -z "$VERSION" ]; then
  echo "usage: bash macOS/build/build.sh <version>" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MAC="$ROOT/macOS"
OUT="$MAC/out"
STAGE="$OUT/stage"
APP="$STAGE/Magpie.app"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# 8.0.0 -> 8.0.0 (AssemblyVersion, CFBundleShortVersionString, CFBundleVersion). A test version 8.0.0-beta.95 keeps
# 8.0.0 as its short version and gets CFBundleVersion 8.0.95 (at most three numbers, as macOS wants).
NUMERIC="$(python3 "$ROOT/common/scripts/release_prep.py" numeric "$VERSION")"
BUNDLE_VERSION="$NUMERIC"
if [[ "$VERSION" =~ -[A-Za-z]+\.([0-9]+)$ ]]; then BUNDLE_VERSION="${NUMERIC%.*}.${BASH_REMATCH[1]}"; fi
echo "Magpie for Mac $VERSION (bundle $NUMERIC / $BUNDLE_VERSION)"

rm -rf "$OUT"
mkdir -p "$STAGE"

# 1. Publish: self-contained .NET 10 for Apple Silicon. Run from macOS/ so macOS/global.json picks SDK 10
#    (the repository root's global.json picks SDK 8 for the Windows app).
(
  cd "$MAC"
  dotnet --version
  dotnet publish Magpie.Mac/Magpie.Mac.csproj -c Release -r osx-arm64 --self-contained true \
    -p:Version="$VERSION" -p:AssemblyVersion="$NUMERIC.0" -p:FileVersion="$NUMERIC.0" \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$OUT/publish"
)
[ -f "$OUT/publish/Magpie" ] || { echo "error: the publish has no Magpie executable" >&2; exit 1; }

# 2. Magpie.app: everything .NET published goes in Contents/MacOS, the icon in Contents/Resources.
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Magpie"
sed -e "s/@SHORT_VERSION@/$NUMERIC/" -e "s/@BUNDLE_VERSION@/$BUNDLE_VERSION/" -e "s/@VERSION@/$VERSION/" \
  "$MAC/build/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
grep -q "<string>$NUMERIC</string>" "$APP/Contents/Info.plist" || { echo "error: Info.plist version not set" >&2; exit 1; }
echo "Assembled $APP"

if [ "$(uname -s)" != "Darwin" ]; then
  echo "Not on macOS: skipping the icon (sips/iconutil), the ad-hoc signature (codesign) and the disk image (hdiutil)."
  echo "Magpie.app is in $APP; macOS/out/Magpie_$VERSION.dmg is made by the macos job in CI or on a Mac."
  exit 0
fi

# 3. Icon: Magpie.icns from the Windows app's 256 px picture.
ICONSET="$OUT/Magpie.iconset"
SRC_PNG="$ROOT/windows/Magpie.App/Assets/app-256.png"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$SRC_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$SRC_PNG" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Magpie.icns"

# 4. Ad-hoc signature (no Apple Developer ID, by choice): Apple Silicon only runs signed code. The first open needs
#    right-click -> Open (macOS/README.md).
# "-exec … +" makes find fail when any codesign fails (with "\;" it would carry on and exit 0).
find "$APP/Contents/MacOS" -type f -name '*.dylib' -exec codesign --force --sign - {} +
codesign --force --sign - "$APP/Contents/MacOS/Magpie"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --verbose=2 "$APP"

# 5. The disk image: Magpie.app and a link to Applications, to drag it across.
DMG_SRC="$OUT/dmg"
mkdir -p "$DMG_SRC"
ditto "$APP" "$DMG_SRC/Magpie.app"
ln -s /Applications "$DMG_SRC/Applications"
# hdiutil on CI runners now and then fails with "Resource busy": try up to three times.
for attempt in 1 2 3; do
  if hdiutil create -volname "Magpie $VERSION" -srcfolder "$DMG_SRC" -ov -format UDZO "$OUT/Magpie_$VERSION.dmg"; then break; fi
  if [ "$attempt" = 3 ]; then echo "error: hdiutil create failed three times" >&2; exit 1; fi
  echo "hdiutil create failed (try $attempt), retrying in 5 s…"; sleep 5
done
hdiutil verify "$OUT/Magpie_$VERSION.dmg"
rm -rf "$DMG_SRC" "$ICONSET"
ls -la "$OUT/Magpie_$VERSION.dmg"
shasum -a 256 "$OUT/Magpie_$VERSION.dmg"
