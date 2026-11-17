#!/usr/bin/env bash
# Installs the build/runtime dependencies (.NET 10 SDK, yt-dlp, ffmpeg, deno),
# then builds the solution and runs the unit tests.
#
# Usage: scripts/setup.sh [--no-deps] [--no-tests] [--no-deno] [-c Debug|Release]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REQUIRED_DOTNET_MAJOR=10

install_deps=1
run_tests=1
want_deno=1
configuration=Debug

usage() { sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-deps)  install_deps=0 ;;
        --no-tests) run_tests=0 ;;
        --no-deno)  want_deno=0 ;;
        -c|--configuration) configuration="${2:?missing value for $1}"; shift ;;
        -h|--help)  usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

SUDO=""
if [[ $EUID -ne 0 ]]; then
    have sudo && SUDO="sudo" || SUDO=""
fi

# True when a .NET SDK with the required major version is installed.
dotnet_ok() {
    have dotnet || return 1
    dotnet --list-sdks 2>/dev/null | grep -Eq "^${REQUIRED_DOTNET_MAJOR}\."
}

# Fallback: Microsoft's dotnet-install script into ~/.dotnet (no root needed).
install_dotnet_user() {
    info "Installing .NET ${REQUIRED_DOTNET_MAJOR} SDK into ~/.dotnet via dotnet-install.sh"
    local tmp
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' RETURN
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh"
    bash "$tmp/dotnet-install.sh" --channel "${REQUIRED_DOTNET_MAJOR}.0" --install-dir "$HOME/.dotnet"
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
    warn "Add this to your shell profile so 'dotnet' is found later:"
    warn '  export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"'
}

install_deno_user() {
    info "Installing deno into ~/.deno (JS runtime used by yt-dlp for YouTube)"
    curl -fsSL https://deno.land/install.sh | sh -s -- --no-modify-path -y
    export PATH="$HOME/.deno/bin:$PATH"
    warn "Add ~/.deno/bin to your PATH (or set the JS runtime path in the app's Settings → Tools)."
}

install_dependencies() {
    local pkgs=()
    if [[ -r /etc/os-release ]]; then . /etc/os-release; fi

    if have pacman; then
        info "Detected pacman (${PRETTY_NAME:-Arch-based})"
        dotnet_ok || pkgs+=(dotnet-sdk)
        have yt-dlp || pkgs+=(yt-dlp)
        have ffmpeg || pkgs+=(ffmpeg)
        [[ $want_deno -eq 1 ]] && ! have deno && pkgs+=(deno)
        [[ ${#pkgs[@]} -gt 0 ]] && $SUDO pacman -S --needed --noconfirm "${pkgs[@]}"
    elif have apt-get; then
        info "Detected apt (${PRETTY_NAME:-Debian-based})"
        dotnet_ok || pkgs+=("dotnet-sdk-${REQUIRED_DOTNET_MAJOR}.0")
        have ffmpeg || pkgs+=(ffmpeg)
        have yt-dlp || pkgs+=(yt-dlp)
        if [[ ${#pkgs[@]} -gt 0 ]]; then
            $SUDO apt-get update
            # dotnet-sdk-10.0 is not in every release's archive; fall back below.
            $SUDO apt-get install -y "${pkgs[@]}" || warn "Some apt packages failed to install; trying fallbacks."
        fi
    elif have dnf; then
        info "Detected dnf (${PRETTY_NAME:-Fedora-based})"
        dotnet_ok || pkgs+=("dotnet-sdk-${REQUIRED_DOTNET_MAJOR}.0")
        have yt-dlp || pkgs+=(yt-dlp)
        have ffmpeg || pkgs+=(ffmpeg)  # needs RPM Fusion on Fedora
        [[ ${#pkgs[@]} -gt 0 ]] && { $SUDO dnf install -y "${pkgs[@]}" || warn "Some dnf packages failed to install; trying fallbacks."; }
    elif have zypper; then
        info "Detected zypper (${PRETTY_NAME:-openSUSE})"
        dotnet_ok || pkgs+=("dotnet-sdk-${REQUIRED_DOTNET_MAJOR}.0")
        have yt-dlp || pkgs+=(yt-dlp)
        have ffmpeg || pkgs+=(ffmpeg)
        [[ ${#pkgs[@]} -gt 0 ]] && { $SUDO zypper --non-interactive install "${pkgs[@]}" || warn "Some zypper packages failed to install; trying fallbacks."; }
    else
        warn "No supported package manager found (pacman, apt, dnf, zypper)."
    fi

    # Fallbacks that work on any distro without root.
    dotnet_ok || install_dotnet_user
    if [[ $want_deno -eq 1 ]] && ! have deno; then install_deno_user; fi

    have yt-dlp || warn "yt-dlp not found. Install it (e.g. 'pipx install yt-dlp') or let the app download it."
    have ffmpeg || warn "ffmpeg not found. Install it with your package manager or let the app download it."
}

cd "$ROOT"

if [[ $install_deps -eq 1 ]]; then
    install_dependencies
else
    info "Skipping dependency installation"
fi

dotnet_ok || die ".NET ${REQUIRED_DOTNET_MAJOR} SDK not found. Install it or run without --no-deps."

info "dotnet $(dotnet --version)"
have yt-dlp && info "yt-dlp $(yt-dlp --version 2>/dev/null || echo '?')"
have ffmpeg && info "$(ffmpeg -hide_banner -version 2>/dev/null | head -n1)"
have deno   && info "$(deno --version 2>/dev/null | head -n1)"

# One-shot build: no lingering build servers / MSBuild nodes.
BUILD_FLAGS=(--disable-build-servers -nodeReuse:false -c "$configuration")

info "Restoring and building ($configuration)"
dotnet build "${BUILD_FLAGS[@]}"

if [[ $run_tests -eq 1 ]]; then
    info "Running tests"
    dotnet test --no-build -c "$configuration"
fi

info "Done. Start the app with:"
echo "    dotnet run --project src/PlaylistSync.App -c $configuration"
