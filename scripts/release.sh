#!/usr/bin/env bash
# Builds self-contained single-file release binaries and packages them into dist/.
#
#   dist/playlist-sync-<version>-linux-x64.tar.gz
#   dist/playlist-sync-<version>-win-x64.zip
#   dist/SHA256SUMS
#
# Usage: scripts/release.sh [--version X.Y.Z] [--rid <rid>]... [--skip-tests]
#   --version     overrides <Version> from Directory.Build.props
#   --rid         runtime identifier to build (repeatable). Default: linux-x64 win-x64
#                 Others that work: linux-arm64, win-arm64
#   --skip-tests  don't run the unit tests first
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_PROJECT="src/PlaylistSync.App/PlaylistSync.App.csproj"
APP_NAME="playlist-sync"

version=""
rids=()
run_tests=1

usage() { sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)    version="${2:?missing value for $1}"; shift ;;
        --rid)        rids+=("${2:?missing value for $1}"); shift ;;
        --skip-tests) run_tests=0 ;;
        -h|--help)    usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
[[ ${#rids[@]} -eq 0 ]] && rids=(linux-x64 win-x64)

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

cd "$ROOT"
have dotnet || die "dotnet not found. Run scripts/setup.sh first."
have tar    || die "tar not found."

if [[ -z "$version" ]]; then
    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n1)"
    [[ -n "$version" ]] || die "Could not read <Version> from Directory.Build.props; pass --version."
fi

PUBLISH_DIR="$ROOT/publish"
DIST_DIR="$ROOT/dist"
rm -rf "$PUBLISH_DIR" "$DIST_DIR"
mkdir -p "$PUBLISH_DIR" "$DIST_DIR"

COMMON_FLAGS=(--disable-build-servers -nodeReuse:false)

if [[ $run_tests -eq 1 ]]; then
    info "Building and running tests"
    dotnet build "${COMMON_FLAGS[@]}" -c Release
    dotnet test --no-build -c Release
fi

# Writes a zip of directory $2's contents (top-level folder $3) to $1.
make_zip() {
    local out="$1" parent="$2" folder="$3"
    if have zip; then
        (cd "$parent" && zip -qr9 "$out" "$folder")
    elif have python3; then
        python3 -I -c 'import shutil,sys; shutil.make_archive(sys.argv[1][:-4], "zip", sys.argv[2], sys.argv[3])' \
            "$out" "$parent" "$folder"
    else
        die "Neither 'zip' nor 'python3' found; can't create $out"
    fi
}

for rid in "${rids[@]}"; do
    info "Publishing $APP_NAME $version for $rid"
    out="$PUBLISH_DIR/$rid"
    dotnet publish "$APP_PROJECT" "${COMMON_FLAGS[@]}" \
        -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=None -p:DebugSymbols=false \
        -p:Version="$version" \
        -o "$out"

    pkg="$APP_NAME-$version-$rid"
    stage="$PUBLISH_DIR/stage/$pkg"
    mkdir -p "$stage"
    cp -a "$out"/. "$stage"/
    # Native NuGet packages (SkiaSharp, HarfBuzzSharp) ship their own .pdb files; users don't need them.
    find "$stage" -name '*.pdb' -delete
    cp README.md "$stage"/
    [[ -f LICENSE ]] && cp LICENSE "$stage"/

    case "$rid" in
        win-*)
            make_zip "$DIST_DIR/$pkg.zip" "$PUBLISH_DIR/stage" "$pkg"
            ;;
        *)
            chmod +x "$stage/$APP_NAME"
            tar -C "$PUBLISH_DIR/stage" -czf "$DIST_DIR/$pkg.tar.gz" "$pkg"
            ;;
    esac
done

info "Writing checksums"
(cd "$DIST_DIR" && sha256sum -- * > SHA256SUMS)

info "Release packages in dist/:"
ls -lh "$DIST_DIR"
