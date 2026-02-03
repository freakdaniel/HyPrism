#!/usr/bin/env bash
set -euo pipefail

cat << "EOF"

.-..-.      .---.       _                
: :; :      : .; :     :_;               
:    :.-..-.:  _.'.--. .-. .--. ,-.,-.,-.
: :: :: :; :: :   : ..': :`._-.': ,. ,. :
:_;:_;`._. ;:_;   :_;  :_;`.__.':_;:_;:_;
       .-. :                             
       `._.'           linux build script

EOF

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-2.0.0}"
ARTIFACTS="$ROOT/artifacts"
RIDS=("win-x64" "linux-x64" "osx-arm64")
AUTO_INSTALL="${AUTO_INSTALL:-0}"
REMOTE_LINUX_HOST="${REMOTE_LINUX_HOST:-${REMOTE_HOST:-}}"
REMOTE_LINUX_PATH="${REMOTE_LINUX_PATH:-${REMOTE_PATH:-~/HyPrism}}"
SKIP_REMOTE="${SKIP_REMOTE:-0}"
ONLY_BUNDLE=0
SKIP_BUILD="${SKIP_BUILD:-0}"

show_help() {
  cat <<'EOF'
Usage: ./build-linux.sh [options]

Options:
  --help                Show this help message and exit
  --auto-install-deps   Auto-install missing dependencies (Ubuntu/Fedora)
  --only-bundle         Build only the bundle (skip all packaging)
  --no-appimage         Skip AppImage packaging
  --no-flatpak          Skip Flatpak packaging
  --no-deb-rpm          Skip deb/rpm packaging
  --only-appimage       Build only AppImage (skip deb/rpm and Flatpak)
  --only-flatpak        Build only Flatpak (skip deb/rpm and AppImage)
  --only-deb-rpm        Build only deb/rpm (skip Flatpak and AppImage)
EOF
}

# Packaging flags (Linux-only packaging step)
DO_APPIMAGE=1
DO_FLATPAK=1
DO_DEB=1

for arg in "$@"; do
  case "$arg" in
    --help|-h)
      show_help
      exit 0
      ;;
    --auto-install-deps)
      AUTO_INSTALL=1
      ;;
    --only-bundle)
      ONLY_BUNDLE=1
      DO_APPIMAGE=0
      DO_FLATPAK=0
      DO_DEB=0
      ;;
    --no-appimage)
      DO_APPIMAGE=0
      ;;
    --no-flatpak)
      DO_FLATPAK=0
      ;;
    --no-deb)
      DO_DEB=0
      ;;
    --only-appimage)
      DO_APPIMAGE=1
      DO_FLATPAK=0
      DO_DEB=0
      ;;
    --only-flatpak)
      DO_APPIMAGE=0
      DO_FLATPAK=1
      DO_DEB=0
      ;;
    --only-deb-rpm)
      DO_APPIMAGE=0
      DO_FLATPAK=0
      DO_DEB=1
      ;;
    *)
      ;;
  esac
done

if [[ "$ONLY_BUNDLE" == "1" ]]; then
  RIDS=("linux-x64" "linux-arm64")
elif [[ "$DO_APPIMAGE" == "1" && "$DO_FLATPAK" == "0" && "$DO_DEB" == "0" ]]; then
  RIDS=("linux-x64")
elif [[ "$DO_FLATPAK" == "1" && "$DO_APPIMAGE" == "0" && "$DO_DEB" == "0" ]]; then
  RIDS=("linux-x64")
elif [[ "$DO_DEB" == "1" && "$DO_APPIMAGE" == "0" && "$DO_FLATPAK" == "0" ]]; then
  RIDS=("linux-x64")
fi

if [[ "$(uname -s)" == "Linux" ]]; then
  detect_distro() {
    if [[ -f /etc/os-release ]]; then
      . /etc/os-release
      echo "${ID:-}"
      return 0
    fi
    echo ""
  }

  install_packages() {
    local ubuntu_pkgs="$1"
    local fedora_pkgs="$2"
    local distro
    distro="$(detect_distro)"

    case "$distro" in
      ubuntu|debian)
        # Non-interactive installs for CI / automation; avoid tzdata and config prompts
        sudo DEBIAN_FRONTEND=noninteractive apt-get update -y
        sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -o Dpkg::Options::="--force-confdef" -o Dpkg::Options::="--force-confold" $ubuntu_pkgs || true
        ;;
      fedora)
        # Use -y to avoid interactive prompts
        sudo dnf -y install $fedora_pkgs || true
        ;;
      *)
        return 1
        ;;
    esac
  }

  ensure_tool() {
    local cmd="$1" ubuntu_pkgs="$2" fedora_pkgs="$3" post_install="$4"
    if command -v "$cmd" >/dev/null 2>&1; then
      return 0
    fi

    if [[ "$AUTO_INSTALL" == "1" ]]; then
      echo "==> Installing dependencies for $cmd"
      if ! install_packages "$ubuntu_pkgs" "$fedora_pkgs"; then
        echo "!! Unsupported distro for auto-install. Please install: $ubuntu_pkgs / $fedora_pkgs"
      fi
      if [[ -n "$post_install" ]]; then
        eval "$post_install" || true
      fi
    else
      echo "ERROR: Missing $cmd. Install it first or use --auto-install-deps option"
      echo "       - Ubuntu/Debian: $ubuntu_pkgs"
      echo "       - Fedora: $fedora_pkgs"
    fi
    return 1
  }

  require_tool() {
    local cmd="$1" ubuntu_pkgs="$2" fedora_pkgs="$3" post_install="$4"
    ensure_tool "$cmd" "$ubuntu_pkgs" "$fedora_pkgs" "$post_install" || true
    if ! command -v "$cmd" >/dev/null 2>&1; then
      echo "ERROR: $cmd is required for this build but is not available." >&2
      exit 1
    fi
  }

  # Packaging helpers often missing on fresh VMs
  if [[ "$ONLY_BUNDLE" != "1" ]]; then
    if [[ "$DO_DEB" == "1" ]]; then
      # fpm requires ruby, ruby-dev, rubygems, build-essential (make/gcc), and rpm (for rpm build)
      require_tool fpm "ruby ruby-dev rubygems build-essential rpm" "ruby ruby-devel rubygems @development-tools rpm-build" "command -v fpm >/dev/null 2>&1 || sudo gem install --no-document fpm"
    fi
    if [[ "$DO_APPIMAGE" == "1" ]]; then
      # Dependencies: appimagetool (pre-built binary), libfuse2, file
      # Note: older ubuntu needs libfuse2 specifically for appimagetool to run
      require_tool appimagetool "appimagetool file" "appimagetool file" "command -v appimagetool >/dev/null 2>&1 || { sudo apt-get update -y || true; sudo apt-get install -y libfuse2 file || true; curl -fsSL -o /tmp/appimagetool \"https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage\"; chmod +x /tmp/appimagetool; sudo mv /tmp/appimagetool /usr/local/bin/appimagetool; }"
    fi
    if [[ "$DO_FLATPAK" == "1" ]]; then
      require_tool flatpak-builder "flatpak flatpak-builder" "flatpak flatpak-builder" ""
      # appstream-compose is required by flatpak-builder for AppStream metadata
      if [[ "$AUTO_INSTALL" == "1" ]]; then
        if [[ "$(detect_distro)" == "ubuntu" || "$(detect_distro)" == "debian" ]]; then
          if apt-cache show appstream-compose >/dev/null 2>&1; then
            sudo DEBIAN_FRONTEND=noninteractive apt-get install -y appstream-compose
          else
            sudo DEBIAN_FRONTEND=noninteractive apt-get install -y appstream appstream-util desktop-file-utils
          fi
        elif [[ "$(detect_distro)" == "fedora" ]]; then
          sudo dnf -y install appstream desktop-file-utils || true
        fi
      fi
      require_tool appstream-compose "appstream appstream-util desktop-file-utils" "appstream desktop-file-utils" ""
    fi
  fi
fi

mkdir -p "$ARTIFACTS"

if [[ "$SKIP_BUILD" == "1" ]]; then
  echo "==> Skipping frontend + publish (SKIP_BUILD=1)"
  if [[ -d "$ROOT/Packaging/flatpak/bundle" && ! -d "$ARTIFACTS/linux-x64/portable" ]]; then
    mkdir -p "$ARTIFACTS/linux-x64/portable"
    cp -R "$ROOT/Packaging/flatpak/bundle"/* "$ARTIFACTS/linux-x64/portable/" || true
  fi


  if [[ ! -f "$ARTIFACTS/linux-x64/portable/HyPrism" ]]; then
    echo "ERROR: artifacts/linux-x64/portable/HyPrism is missing. Run --only-bundle first or provide bundle." >&2
    exit 1
  fi
else
  # Check for dotnet
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "ERROR: dotnet is required but not found. Please install .NET SDK." >&2
    exit 1
  fi

  echo "==> Restoring and publishing project"
  dotnet restore "$ROOT/HyPrism.csproj"

  for rid in "${RIDS[@]}"; do
    out="$ARTIFACTS/$rid/portable"
    echo "--> Publishing $rid to $out"
    dotnet publish "$ROOT/HyPrism.csproj" -c Release -r "$rid" --self-contained true \
      /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfExtract=true \
      -o "$out"

    case "$rid" in
      win-*)
        (cd "$out" && zip -9 -r "$ARTIFACTS/HyPrism-${rid}-portable.zip" .)
        ;;
      osx-*)
        (cd "$out" && zip -9 -r "$ARTIFACTS/HyPrism-${rid}-portable.zip" .)
        ;;
      linux-*)
        tar -czf "$ARTIFACTS/HyPrism-${rid}-portable.tar.gz" -C "$out" .
        ;;
    esac
  done
fi

if [[ "$ONLY_BUNDLE" == "1" && "$(uname -s)" == "Linux" ]]; then
  echo "==> Preparing bundle only"
  LINUX_OUT="$ARTIFACTS/linux-x64/portable"
  rm -rf "$ROOT/Packaging/flatpak/bundle"
  mkdir -p "$ROOT/Packaging/flatpak/bundle"
  cp -R "$LINUX_OUT"/* "$ROOT/Packaging/flatpak/bundle/" || true
  cp "$ROOT/Packaging/flatpak/dev.hyprism.HyPrism."* "$ROOT/Packaging/flatpak/bundle/" || true
  chmod +x "$ROOT/Packaging/flatpak/bundle/HyPrism" || true
  echo "Bundle ready at $ROOT/Packaging/flatpak/bundle"
  echo "Done. Artifacts in $ARTIFACTS"
  exit 0
fi

# Linux-only packaging helpers
if [[ "$(uname -s)" == "Linux" ]]; then
  LINUX_OUT="$ARTIFACTS/linux-x64/portable"
  PKGROOT="$ARTIFACTS/linux-x64/pkgroot"
  ICON_SRC="$ROOT/Packaging/flatpak/dev.hyprism.HyPrism.png"
  DESKTOP_SRC="$ROOT/Packaging/flatpak/dev.hyprism.HyPrism.desktop"

  echo "==> Staging Linux package tree"
  rm -rf "$PKGROOT"
  mkdir -p "$PKGROOT/opt/hyprism" "$PKGROOT/usr/bin" "$PKGROOT/usr/share/applications" "$PKGROOT/usr/share/icons/hicolor/256x256/apps"
  cp -R "$LINUX_OUT"/* "$PKGROOT/opt/hyprism/"
  ln -sf /opt/hyprism/HyPrism "$PKGROOT/usr/bin/hyprism"
  [[ -f "$DESKTOP_SRC" ]] && cp "$DESKTOP_SRC" "$PKGROOT/usr/share/applications/dev.hyprism.HyPrism.desktop"
  [[ -f "$ICON_SRC" ]] && cp "$ICON_SRC" "$PKGROOT/usr/share/icons/hicolor/256x256/apps/dev.hyprism.HyPrism.png"

  if [[ "$DO_DEB" == "1" ]]; then
    echo "==> Building .deb and .rpm via fpm"
    fpm -s dir -t deb -n hyprism -v "$VERSION" -C "$PKGROOT" \
      --description "HyPrism launcher" --url "https://github.com/yyyumeniku/HyPrism" \
      -p "$ARTIFACTS/HyPrism-linux-x64.deb" .
    fpm -s dir -t rpm -n hyprism -v "$VERSION" -C "$PKGROOT" \
      --description "HyPrism launcher" --url "https://github.com/yyyumeniku/HyPrism" \
      -p "$ARTIFACTS/HyPrism-linux-x64.rpm" .
  else
    echo "==> Skipping deb/rpm generation (--no-deb/--only-*)"
  fi

  if [[ "$DO_APPIMAGE" == "1" ]]; then
    echo "==> Building AppImage"
    APPDIR="$ARTIFACTS/linux-x64/AppDir"
    
    # Prepare AppDir Structure
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/bin"
    mkdir -p "$APPDIR/usr/share/applications"
    mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
        
    # Copy from portable output
    cp -r "$LINUX_OUT/." "$APPDIR/usr/bin/"
    chmod +x "$APPDIR/usr/bin/HyPrism"
    
    # Copy desktop file and Icon
    [[ -f "$DESKTOP_SRC" ]] && cp "$DESKTOP_SRC" "$APPDIR/usr/share/applications/dev.hyprism.HyPrism.desktop"
    [[ -f "$ICON_SRC" ]] && cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/dev.hyprism.HyPrism.png"
    [[ -f "$ICON_SRC" ]] && cp "$ICON_SRC" "$APPDIR/dev.hyprism.HyPrism.png"
    
    # Symlink desktop file to root so AppImage finds it
    (cd "$APPDIR" && ln -sf usr/share/applications/dev.hyprism.HyPrism.desktop HyPrism.desktop)
    
    cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
exec "$(dirname "$0")/usr/bin/HyPrism" "$@"
EOF
    chmod +x "$APPDIR/AppRun"
    # Remove existing artifact to avoid interactive overwrite prompts
    rm -f "$ARTIFACTS/HyPrism-linux-x64.AppImage"
    appimagetool "$APPDIR" "$ARTIFACTS/HyPrism-linux-x64.AppImage"
  else
    echo "==> Skipping AppImage (--no-appimage/--only-*)"
  fi

  if [[ "$DO_FLATPAK" == "1" ]]; then
    echo "==> Building Flatpak"
    FLATPAK_STAGE="$ARTIFACTS/linux-x64/flatpak-build"
    FLATPAK_REPO="$ARTIFACTS/flatpak-repo"
    # Use user remotes to avoid system-remote lookups in CI
    flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
    flatpak remote-add --user --if-not-exists flathub-beta https://flathub.org/beta-repo/flathub-beta.flatpakrepo
    rm -rf "$ROOT/Packaging/flatpak/bundle"
    mkdir -p "$ROOT/Packaging/flatpak/bundle"
    cp -R "$LINUX_OUT"/* "$ROOT/Packaging/flatpak/bundle/"
    cp "$ROOT/Packaging/flatpak/dev.hyprism.HyPrism."* "$ROOT/Packaging/flatpak/bundle/" || true
    chmod +x "$ROOT/Packaging/flatpak/bundle/HyPrism" || true
    flatpak-builder --user --force-clean "$FLATPAK_STAGE" \
      --install-deps-from=flathub --install-deps-from=flathub-beta \
      "$ROOT/Packaging/flatpak/dev.hyprism.HyPrism.json" --repo="$FLATPAK_REPO"
    echo "==> Bundling Flatpak"
    rm -f "$ARTIFACTS/HyPrism-linux-x64.flatpak"
    flatpak build-bundle "$FLATPAK_REPO" "$ARTIFACTS/HyPrism-linux-x64.flatpak" dev.hyprism.HyPrism \
      --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo
  else
    echo "==> Skipping Flatpak (--no-flatpak/--only-*)"
  fi
fi

# Optional: trigger remote Linux build (e.g., Parallels VM) after local steps
if [[ -n "$REMOTE_LINUX_HOST" && "$SKIP_REMOTE" != "1" ]]; then
  echo "==> Triggering remote build on $REMOTE_LINUX_HOST ($REMOTE_LINUX_PATH)"
  ssh -o BatchMode=yes "$REMOTE_LINUX_HOST" "cd '$REMOTE_LINUX_PATH' && SKIP_REMOTE=1 ./Scripts/build-linux.sh --auto-install-deps" || {
    echo "!! Remote build failed on $REMOTE_LINUX_HOST" >&2
    exit 1
  }
fi

echo "Done. Artifacts in $ARTIFACTS"
