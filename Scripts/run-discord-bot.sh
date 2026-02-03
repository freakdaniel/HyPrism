#!/bin/bash
# HyPrism Discord Bot Runner Script
# This script runs the Discord bot that handles announcements

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
ENV_FILE="$PROJECT_ROOT/.env"

# Load environment variables
if [ -f "$ENV_FILE" ]; then
    echo "Loading environment from $ENV_FILE"
    export $(grep -v '^#' "$ENV_FILE" | xargs)
else
    echo "Error: .env file not found at $ENV_FILE"
    echo "Please create a .env file with DISCORD_BOT_TOKEN and DISCORD_CHANNEL_ID"
    exit 1
fi

# Check required variables
if [ -z "$DISCORD_BOT_TOKEN" ]; then
    echo "Error: DISCORD_BOT_TOKEN not set in .env"
    exit 1
fi

if [ -z "$DISCORD_CHANNEL_ID" ]; then
    echo "Error: DISCORD_CHANNEL_ID not set in .env"
    exit 1
fi

echo "Discord Bot Configuration:"
echo "  Channel ID: $DISCORD_CHANNEL_ID"
echo "  Token: [HIDDEN]"
echo ""

# The bot token is used by the HyPrism launcher to fetch announcements
# The launcher reads from the .env file at runtime
echo "The Discord bot token is configured."
echo "The HyPrism launcher will use this token to fetch announcements from Discord."
echo ""
echo "To post announcements:"
echo "  1. Go to your Discord channel (ID: $DISCORD_CHANNEL_ID)"
echo "  2. Post a message - the launcher will display it as an announcement"
echo "  3. React with ❌ to hide a message from the launcher"
echo ""
echo "Bot is ready for use with HyPrism launcher."
