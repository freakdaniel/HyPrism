#!/usr/bin/env bash
set -euo pipefail

# Build only AppImage and Flatpak (requires native Ubuntu, not Docker)
# Run this script in your Ubuntu VM after building the binaries

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-2.0.0}"
ARTIFACTS="$ROOT/artifacts"
LINUX_OUT="$ARTIFACTS/linux-x64/portable"
ICON_SRC="$ROOT/packaging/flatpak/dev.hyprism.HyPrism.png"
DESKTOP_SRC="$ROOT/packaging/flatpak/dev.hyprism.HyPrism.desktop"

if [[ ! -d "$LINUX_OUT" ]] || [[ ! -f "$LINUX_OUT/HyPrism" ]]; then
  echo "Error: Linux binaries not found at $LINUX_OUT"
  echo "Please run the main build script first to generate binaries."
  exit 1
fi

echo "==> Building AppImage"
if command -v appimagetool >/dev/null 2>&1; then
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
  
  ARCH=x86_64 appimagetool -n "$APPDIR" "$ARTIFACTS/HyPrism-linux-x64.AppImage"
  echo "✓ AppImage created: $ARTIFACTS/HyPrism-linux-x64.AppImage"
else
  echo "Warning: appimagetool not found. Install with:"
  echo "  curl -fsSL -o /tmp/appimagetool https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
  echo "  chmod +x /tmp/appimagetool"
  echo "  sudo mv /tmp/appimagetool /usr/local/bin/appimagetool"
fi

echo ""
echo "==> Building Flatpak"
if command -v flatpak-builder >/dev/null 2>&1; then
  FLATPAK_STAGE="$ARTIFACTS/linux-x64/flatpak-build"
  FLATPAK_REPO="$ARTIFACTS/flatpak-repo"
  
  # Copy full publish output to flatpak packaging directory
  rm -rf "$ROOT/packaging/flatpak/bundle"
  mkdir -p "$ROOT/packaging/flatpak/bundle"
  cp -R "$LINUX_OUT"/* "$ROOT/packaging/flatpak/bundle/"
  
  # Build flatpak with --disable-rofiles-fuse for Docker compatibility (also works on native)
  flatpak-builder --force-clean --disable-rofiles-fuse "$FLATPAK_STAGE" \
    "$ROOT/packaging/flatpak/dev.hyprism.HyPrism.json" --repo="$FLATPAK_REPO"
  
  # Export to repo
  flatpak build-export "$FLATPAK_REPO" "$FLATPAK_STAGE"
  
  # Create bundle
  flatpak build-bundle "$FLATPAK_REPO" "$ARTIFACTS/HyPrism-linux-x64.flatpak" \
    dev.hyprism.HyPrism --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo
  
  echo "✓ Flatpak created: $ARTIFACTS/HyPrism-linux-x64.flatpak"
else
  echo "Warning: flatpak-builder not found. Install with:"
  echo "  sudo apt-get install flatpak flatpak-builder"
  echo "  flatpak remote-add --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo"
  echo "  flatpak install -y flathub org.freedesktop.Sdk//22.08 org.freedesktop.Platform//22.08"
fi

echo ""
echo "Done! Check artifacts:"
[[ -f "$ARTIFACTS/HyPrism-linux-x64.AppImage" ]] && echo "  ✓ AppImage: $ARTIFACTS/HyPrism-linux-x64.AppImage"
[[ -f "$ARTIFACTS/HyPrism-linux-x64.flatpak" ]] && echo "  ✓ Flatpak: $ARTIFACTS/HyPrism-linux-x64.flatpak"
