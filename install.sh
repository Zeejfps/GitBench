#!/bin/sh
# Installs the latest DiffDino release on macOS or Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/Zeejfps/GitBench/main/install.sh | sh
#
# macOS: downloads the installer package for this Mac (Apple Silicon or Intel) and installs
#        it into /Applications, which asks for your password.
# Linux: downloads the AppImage into ~/.local/bin (override with DIFFDINO_INSTALL_DIR) and
#        adds DiffDino to the application menu. To remove all of it again:
#
#   curl -fsSL https://raw.githubusercontent.com/Zeejfps/GitBench/main/install.sh | sh -s -- --uninstall
#
# Everything lives in main(), called on the last line, so a download cut off midway runs
# nothing rather than half a script.

set -eu

REPO="Zeejfps/GitBench"
BASE_URL="https://github.com/$REPO/releases/latest/download"

say() { printf '%s\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

download() { # url dest
    if command -v curl >/dev/null 2>&1; then
        curl -fL --progress-bar -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then
        wget -q --show-progress -O "$2" "$1"
    else
        fail "curl or wget is required"
    fi
}

install_macos() {
    case "$(uname -m)" in
        arm64) arch=arm64 ;;
        x86_64) arch=x64 ;;
        *) fail "unsupported Mac architecture: $(uname -m)" ;;
    esac

    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    pkg="$tmp/DiffDino-Setup.pkg"

    say "Downloading DiffDino for macOS ($arch)..."
    download "$BASE_URL/DiffDino-osx-$arch-Setup.pkg" "$pkg"

    say "Installing (you may be asked for your password)..."
    sudo installer -pkg "$pkg" -target /
    say "DiffDino is installed in /Applications."
}

linux_paths() {
    dir="${DIFFDINO_INSTALL_DIR:-$HOME/.local/bin}"
    dest="$dir/DiffDino-linux-x64.AppImage"
    data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
    desktop_file="$data_home/applications/DiffDino.desktop"
    icon_file="$data_home/icons/DiffDino.png"
}

# A value in a .desktop file escapes its backslashes (Desktop Entry spec, "Possible value
# types").
desktop_string() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g'
}

# Exec is stricter: the path is double-quoted, with \ " ` $ backslash-escaped inside the
# quotes, then string-escaped as above, and a literal % doubled.
desktop_exec() {
    quoted=$(printf '%s' "$1" | sed -e 's/[\\"`$]/\\&/g' -e 's/%/%%/g')
    desktop_string "\"$quoted\""
}

# Writes the menu entry the app itself keeps up to date on launch (ZGF.AppUtils.LinuxDesktopEntry, via Program.cs);
# the two must stay identical, or every launch rewrites it.
install_desktop_entry() {
    mkdir -p "$data_home/applications" "$data_home/icons"

    # The AppImage runtime extracts its own icon without starting the app. No icon is not
    # worth failing the install over: the entry still works, with a generic icon.
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    if (cd "$tmp" && "$dest" --appimage-extract .DirIcon >/dev/null 2>&1) && [ -s "$tmp/squashfs-root/.DirIcon" ]; then
        cp -f "$tmp/squashfs-root/.DirIcon" "$icon_file"
    else
        say "Note: could not extract the app icon; the menu entry will use a generic one."
    fi

    cat > "$desktop_file.part" <<EOF
[Desktop Entry]
Type=Application
Name=DiffDino
Comment=A desktop Git client
Exec=$(desktop_exec "$dest")
TryExec=$(desktop_string "$dest")
Icon=$(desktop_string "$icon_file")
StartupWMClass=DiffDino
Categories=Development;RevisionControl;
Terminal=false
X-DiffDino-Managed=true
EOF
    mv -f "$desktop_file.part" "$desktop_file"
    say "Added DiffDino to the application menu."
}

install_linux() {
    case "$(uname -m)" in
        x86_64 | amd64) ;;
        *) fail "only x86_64 Linux builds are published (this machine is $(uname -m))" ;;
    esac

    linux_paths
    mkdir -p "$dir"

    # Download beside the target, then rename, so a failed download never clobbers a
    # working install. 755 so a double-click in the file manager runs it.
    say "Downloading DiffDino for Linux..."
    download "$BASE_URL/DiffDino-linux-x64.AppImage" "$dest.part"
    chmod 755 "$dest.part"
    mv -f "$dest.part" "$dest"
    say "DiffDino is installed at $dest"

    install_desktop_entry

    case ":$PATH:" in
        *":$dir:"*) ;;
        *) say "Note: $dir is not on your PATH." ;;
    esac
}

uninstall_linux() {
    linux_paths
    rm -f "$dest" "$desktop_file" "$icon_file"
    say "Removed DiffDino and its menu entry. Your settings in ~/.config/DiffDino are kept."
}

main() {
    case "$(uname -s)" in
        Darwin)
            [ "${1:-}" = --uninstall ] && fail "--uninstall is Linux only; on macOS, move DiffDino from /Applications to the Trash."
            install_macos
            ;;
        Linux)
            if [ "${1:-}" = --uninstall ]; then uninstall_linux; else install_linux; fi
            ;;
        *) fail "unsupported OS: $(uname -s). On Windows, use install.ps1." ;;
    esac
}

main "$@"
