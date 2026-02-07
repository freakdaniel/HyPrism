# Installation Guide

## System Requirements

| | Minimum | Recommended |
|---|---------|-------------|
| **OS** | Windows 10, Linux (Modern), macOS 12 | Windows 11, Ubuntu 22.04 LTS, macOS 14 |
| **RAM** | 2 GB | 4 GB |
| **Disk** | 200 MB (launcher only) | 10 GB+ (launcher + game) |
| **Graphics** | DirectX 10 / OpenGL 3.3 | DirectX 11+ / Vulkan |
| **.NET** | .NET 10 Runtime | .NET 10 SDK (for building) |

---

## 🪟 Windows Installation

### Option A: Installer

1. **Download** `HyPrism-Setup.exe` from the [releases page](https://github.com/yyyumeniku/HyPrism/releases)
2. **Run** the installer
   > Windows SmartScreen may warn about unsigned file — click "Run anyway"
3. **Done!** Shortcut will appear on desktop

### Option B: Portable Version

1. Download `HyPrism-win-x64.zip`
2. Extract to any folder
3. Run `HyPrism.exe`

---

## 🐧 Linux Installation

### Option A: Flatpak (recommended)

Flatpak provides isolation and all dependencies bundled.

```bash
# Add Flathub (if not already added)
flatpak remote-add --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo

# Install HyPrism
flatpak install dev.hyprism.HyPrism
```

### Option B: Portable Binary

```bash
# Download and extract
wget https://github.com/yyyumeniku/HyPrism/releases/download/latest/HyPrism-linux-x64.tar.gz
tar -xzf HyPrism-linux-x64.tar.gz
cd HyPrism

# Set permissions and run
chmod +x HyPrism
./HyPrism
```

### Possible Dependencies

Depending on distribution, you may need:

```bash
# Ubuntu/Debian
sudo apt install libicu-dev libssl-dev libfontconfig1

# Fedora
sudo dnf install icu libssl fontconfig

# Arch
sudo pacman -S icu openssl fontconfig
```

### .NET Version Issues

If you encounter .NET version errors:

```bash
export DOTNET_ROLL_FORWARD=Major
./HyPrism
```

---

## 🍎 macOS Installation

### Installation Steps

1. **Download** `HyPrism.dmg`
2. **Mount** — double-click the `.dmg`
3. **Install** — drag the HyPrism icon to the Applications folder

### Security Warning

On first launch, macOS may block the app from "Unknown Developer":

1. Open **System Settings** → **Privacy & Security**
2. Scroll down and click **"Open Anyway"** for HyPrism

---

## 🔧 Building from Source

### Requirements

- Git
- .NET 10 SDK

### Steps

```bash
# Clone
git clone https://github.com/yyyumeniku/HyPrism.git
cd HyPrism

# Build
dotnet build

# Run
dotnet run
```

### Publishing

```bash
# Self-contained for Linux
dotnet publish -c Release -r linux-x64 --self-contained

# Self-contained for Windows
dotnet publish -c Release -r win-x64 --self-contained

# Self-contained for macOS
dotnet publish -c Release -r osx-x64 --self-contained
```

---

## 📁 Data Location

After installation, HyPrism creates a data folder:

| OS | Path |
|----|------|
| Windows | `%APPDATA%\HyPrism\` |
| Linux | `~/.config/HyPrism/` |
| macOS | `~/Library/Application Support/HyPrism/` |

Contents:
- `config.json` — settings
- `profiles/` — user profiles
- `Instances/` — installed game versions
- `Logs/` — logs

---

## ❓ Troubleshooting

### Launcher Won't Start

1. Check .NET 10 Runtime installation
2. Check logs in `Logs/` folder
3. Try `DOTNET_ROLL_FORWARD=Major`

### White/Black Screen

Possible graphics driver issue:
```bash
# Force software rendering
export AVALONIA_RENDER=software
./HyPrism
```

### Game Download Issues

Check:
- Internet connection
- Free disk space
- Permissions on Instances folder

---

## 📚 Additional Resources

- [Configuration.md](Configuration.md) — Configuration settings
- [Features.md](../General/Features.md) — Feature description
