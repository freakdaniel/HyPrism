#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-2.0.0}"
ARTIFACTS="$ROOT/artifacts"
RIDS=("linux-x64" "linux-arm64")

require_sudo() {
  if ! command -v sudo >/dev/null 2>&1; then
    echo "sudo is required to install dependencies." >&2
    exit 1
  fi
}

apt_install() {
  sudo apt-get update -y
  sudo apt-get install -y "$@"
}

ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    return 0
  fi

  require_sudo
  if [[ -f /etc/os-release ]]; then
    . /etc/os-release
    local version_id="${VERSION_ID:-}" 
    if [[ -n "$version_id" ]]; then
      curl -fsSL -o /tmp/packages-microsoft-prod.deb "https://packages.microsoft.com/config/ubuntu/${version_id}/packages-microsoft-prod.deb"
      sudo dpkg -i /tmp/packages-microsoft-prod.deb
    fi
  fi

  apt_install apt-transport-https ca-certificates
  sudo apt-get update -y
  sudo apt-get install -y dotnet-sdk-8.0
}

ensure_node() {
  if command -v node >/dev/null 2>&1 && command -v npm >/dev/null 2>&1; then
    local major
    major="$(node -v | sed -E 's/^v([0-9]+).*/\1/')"
    if [[ "$major" -ge 18 ]]; then
      return 0
    fi
  fi

  require_sudo
  apt_install curl
  curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
  sudo apt-get install -y nodejs
}

ensure_fpm() {
  if command -v fpm >/dev/null 2>&1; then
    return 0
  fi
  require_sudo
  apt_install ruby ruby-dev build-essential rpm rubygems
  sudo gem install --no-document fpm
}

ensure_appimagetool() {
  if command -v appimagetool >/dev/null 2>&1; then
    return 0
  fi

  local arch
  arch="$(uname -m)"
  if [[ "$arch" != "x86_64" ]]; then
    echo "appimagetool is only available for x86_64. Skipping AppImage for $arch."
    return 0
  fi

  require_sudo
  curl -fsSL -o /tmp/appimagetool "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
  sudo install -m 0755 /tmp/appimagetool /usr/local/bin/appimagetool
}

ensure_flatpak() {
  if command -v flatpak-builder >/dev/null 2>&1; then
    if flatpak info org.freedesktop.Sdk >/dev/null 2>&1; then
      return 0
    fi
  fi
  require_sudo
  apt_install flatpak flatpak-builder appstream-util desktop-file-utils

  # Ensure Flathub remote exists
  if ! flatpak remotes | grep -q "^flathub"; then
    flatpak remote-add --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
  fi

  # Install required SDK/Platform for Flatpak build
  local arch
  arch="$(uname -m)"
  local flatpak_arch="x86_64"
  if [[ "$arch" == "aarch64" || "$arch" == "arm64" ]]; then
    flatpak_arch="aarch64"
  fi
  flatpak install -y --noninteractive flathub \
    "org.freedesktop.Sdk//22.08" \
    "org.freedesktop.Platform//22.08" \
    "org.freedesktop.Sdk.Extension.golang//22.08" \
    "org.freedesktop.Sdk.Extension.node18//22.08" \
    --arch="$flatpak_arch" || true
}

rm -rf "$ARTIFACTS"
mkdir -p "$ARTIFACTS"

echo "==> Installing build dependencies"
ensure_node
ensure_dotnet
ensure_fpm
ensure_appimagetool
ensure_flatpak
apt_install zip unzip tar git curl

echo "==> Building frontend"
if [[ -f "$ROOT/frontend/package-lock.json" ]]; then
  (cd "$ROOT/frontend" && npm ci)
else
  (cd "$ROOT/frontend" && npm install)
fi
(cd "$ROOT/frontend" && npm run build)

echo "==> Restoring backend"
dotnet restore "$ROOT/HyPrism.csproj"

for rid in "${RIDS[@]}"; do
  out="$ARTIFACTS/$rid/portable"
  echo "--> Publishing $rid to $out"
  dotnet publish "$ROOT/HyPrism.csproj" -c Release -r "$rid" --self-contained true \
    /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$out"

  tar -czf "$ARTIFACTS/HyPrism-${rid}-portable.tar.gz" -C "$out" .
done

# Linux-x64 packaging (deb, rpm, AppImage, Flatpak)
LINUX_OUT="$ARTIFACTS/linux-x64/portable"
PKGROOT="$ARTIFACTS/linux-x64/pkgroot"
ICON_SRC="$ROOT/packaging/flatpak/dev.hyprism.HyPrism.png"
DESKTOP_SRC="$ROOT/packaging/flatpak/dev.hyprism.HyPrism.desktop"

if [[ -d "$LINUX_OUT" ]]; then
  echo "==> Staging Linux package tree (x64)"
  rm -rf "$PKGROOT"
  mkdir -p "$PKGROOT/opt/hyprism" "$PKGROOT/usr/bin" "$PKGROOT/usr/share/applications" "$PKGROOT/usr/share/icons/hicolor/256x256/apps"
  cp -R "$LINUX_OUT"/* "$PKGROOT/opt/hyprism/"
  ln -sf /opt/hyprism/HyPrism "$PKGROOT/usr/bin/hyprism"
  [[ -f "$DESKTOP_SRC" ]] && cp "$DESKTOP_SRC" "$PKGROOT/usr/share/applications/dev.hyprism.HyPrism.desktop"
  [[ -f "$ICON_SRC" ]] && cp "$ICON_SRC" "$PKGROOT/usr/share/icons/hicolor/256x256/apps/dev.hyprism.HyPrism.png"

  if command -v fpm >/dev/null 2>&1; then
    echo "==> Building .deb and .rpm"
    fpm -s dir -t deb -n hyprism -v "$VERSION" -C "$PKGROOT" \
      --description "HyPrism launcher" --url "https://github.com/yyyumeniku/HyPrism" \
      -p "$ARTIFACTS/HyPrism-linux-x64.deb" .
    fpm -s dir -t rpm -n hyprism -v "$VERSION" -C "$PKGROOT" \
      --description "HyPrism launcher" --url "https://github.com/yyyumeniku/HyPrism" \
      -p "$ARTIFACTS/HyPrism-linux-x64.rpm" .
  fi

  if command -v appimagetool >/dev/null 2>&1; then
    echo "==> Building AppImage"
    APPDIR="$ARTIFACTS/linux-x64/AppDir"
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/hyprism" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
    cp -R "$LINUX_OUT"/* "$APPDIR/usr/lib/hyprism/"
    chmod +x "$APPDIR/usr/lib/hyprism/HyPrism"
    ln -sf ../lib/hyprism/HyPrism "$APPDIR/usr/bin/HyPrism"
    [[ -f "$DESKTOP_SRC" ]] && cp "$DESKTOP_SRC" "$APPDIR/usr/share/applications/dev.hyprism.HyPrism.desktop"
    [[ -f "$ICON_SRC" ]] && cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/dev.hyprism.HyPrism.png"
    cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
exec "$(dirname "$0")/usr/lib/hyprism/HyPrism" "$@"
EOF
    chmod +x "$APPDIR/AppRun"
    (cd "$APPDIR" && ln -sf usr/share/applications/dev.hyprism.HyPrism.desktop HyPrism.desktop)
    # appimagetool is already extracted, just run it
    ARCH=x86_64 appimagetool -n "$APPDIR" "$ARTIFACTS/HyPrism-linux-x64.AppImage" || \
      echo "Warning: AppImage creation failed, skipping"
  fi

  if command -v flatpak-builder >/dev/null 2>&1; then
    echo "==> Building Flatpak"
    FLATPAK_STAGE="$ARTIFACTS/linux-x64/flatpak-build"
    FLATPAK_REPO="$ARTIFACTS/flatpak-repo"
    rm -rf "$ROOT/packaging/flatpak/bundle"
    mkdir -p "$ROOT/packaging/flatpak/bundle"
    cp -R "$LINUX_OUT"/* "$ROOT/packaging/flatpak/bundle/"
    # Use --disable-rofiles-fuse for Docker compatibility
    flatpak-builder --force-clean --disable-rofiles-fuse "$FLATPAK_STAGE" "$ROOT/packaging/flatpak/dev.hyprism.HyPrism.json" --repo="$FLATPAK_REPO" || \
      echo "Warning: Flatpak build failed, skipping"
    if [[ -d "$FLATPAK_REPO" ]]; then
      flatpak build-export "$FLATPAK_REPO" "$FLATPAK_STAGE" || true
      flatpak build-bundle "$FLATPAK_REPO" "$ARTIFACTS/HyPrism-linux-x64.flatpak" dev.hyprism.HyPrism --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo || \
        echo "Warning: Flatpak bundle creation failed"
    fi
  fi
fi

echo "Done. Artifacts are located in: $ARTIFACTS"
echo "Contents:"
ls -la "$ARTIFACTS"