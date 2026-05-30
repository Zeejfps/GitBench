#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 6 || $# -gt 7 ]]; then
  echo "usage: $0 <publish-dir> <bundle-dir> <exe-name> <bundle-id> <display-name> <version> [icon-png]" >&2
  exit 2
fi

publish_dir="$1"
bundle_dir="$2"
exe_name="$3"
bundle_id="$4"
display_name="$5"
version="$6"
icon_src="${7:-}"

contents="$bundle_dir/Contents"
macos="$contents/MacOS"
resources="$contents/Resources"

rm -rf "$bundle_dir"
mkdir -p "$macos" "$resources"

rsync -a \
  --exclude='*.pdb' \
  --exclude='*.runtimeconfig.json' \
  --exclude='*.dSYM' \
  --exclude='.DS_Store' \
  "$publish_dir"/ "$macos"/

icon_plist_entry=""
if [[ -n "$icon_src" ]]; then
  if [[ ! -f "$icon_src" ]]; then
    echo "icon source not found: $icon_src" >&2
    exit 1
  fi
  icon_name="$(basename "$icon_src" .png)"
  work_dir="$(mktemp -d)"
  iconset="$work_dir/$icon_name.iconset"
  mkdir -p "$iconset"
  # Apple's required iconset layout: icon_<pt>x<pt>.png and icon_<pt>x<pt>@2x.png
  # where the @2x variant is rendered at double the point size.
  while read -r out_name px; do
    sips -z "$px" "$px" "$icon_src" --out "$iconset/$out_name" >/dev/null
  done <<EOF
icon_16x16.png 16
icon_16x16@2x.png 32
icon_32x32.png 32
icon_32x32@2x.png 64
icon_128x128.png 128
icon_128x128@2x.png 256
icon_256x256.png 256
icon_256x256@2x.png 512
icon_512x512.png 512
icon_512x512@2x.png 1024
EOF
  iconutil -c icns "$iconset" -o "$resources/$icon_name.icns"
  rm -rf "$work_dir"
  icon_plist_entry="  <key>CFBundleIconFile</key>
  <string>$icon_name</string>
"
fi

cat > "$contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>$exe_name</string>
  <key>CFBundleIdentifier</key>
  <string>$bundle_id</string>
  <key>CFBundleName</key>
  <string>$display_name</string>
  <key>CFBundleDisplayName</key>
  <string>$display_name</string>
${icon_plist_entry}  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSMinimumSystemVersion</key>
  <string>11.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
EOF

chmod +x "$macos/$exe_name"

echo "Created macOS app bundle: $bundle_dir"
