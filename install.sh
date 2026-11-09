#!/bin/sh
# Installs the latest DiffDino release on macOS or Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/Zeejfps/GitBench/main/install.sh | sh
#
# macOS: downloads the installer package for this Mac (Apple Silicon or Intel) and installs
#        it into /Applications, which asks for your password.
# Linux: downloads the AppImage into ~/.local/bin (override with DIFFDINO_INSTALL_DIR).
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

install_linux() {
    case "$(uname -m)" in
        x86_64 | amd64) ;;
        *) fail "only x86_64 Linux builds are published (this machine is $(uname -m))" ;;
    esac

    dir="${DIFFDINO_INSTALL_DIR:-$HOME/.local/bin}"
    dest="$dir/DiffDino-linux-x64.AppImage"
    mkdir -p "$dir"

    # Download beside the target, then rename, so a failed download never clobbers a
    # working install.
    say "Downloading DiffDino for Linux..."
    download "$BASE_URL/DiffDino-linux-x64.AppImage" "$dest.part"
    chmod +x "$dest.part"
    mv -f "$dest.part" "$dest"
    say "DiffDino is installed at $dest"

    case ":$PATH:" in
        *":$dir:"*) ;;
        *) say "Note: $dir is not on your PATH." ;;
    esac
}

main() {
    case "$(uname -s)" in
        Darwin) install_macos ;;
        Linux) install_linux ;;
        *) fail "unsupported OS: $(uname -s). On Windows, use install.ps1." ;;
    esac
}

main "$@"
