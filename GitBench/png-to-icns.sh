#!/usr/bin/env bash
set -euo pipefail

# Converts a square PNG into a macOS .icns (requires macOS `sips` + `iconutil`).
# Shared by build-macos-bundle.sh (local .app builds) and the release workflow
# (vpk pack --icon, which rejects anything without an .icns extension).

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <icon.png> <out.icns>" >&2
  exit 2
fi

icon_src="$1"
out="$2"

if [[ ! -f "$icon_src" ]]; then
  echo "icon source not found: $icon_src" >&2
  exit 1
fi

work_dir="$(mktemp -d)"
iconset="$work_dir/icon.iconset"
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

iconutil -c icns "$iconset" -o "$out"
rm -rf "$work_dir"
echo "Created $out"
