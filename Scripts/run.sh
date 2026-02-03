#!/bin/bash

# HyPrism Launcher Script
# This script builds and runs the HyPrism launcher

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

echo "Building HyPrism Launcher..."

# Build and run Avalonia App
echo "Building project..."
dotnet build

echo "Starting HyPrism..."
dotnet run
