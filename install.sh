#!/usr/bin/env bash
# Twig CLI installer for Linux and macOS
# Usage: curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash

set -euo pipefail

REPO="PolyphonyRequiem/twig"
INSTALL_DIR="$HOME/.twig/bin"

echo "Installing twig..."

# Detect OS
OS="$(uname -s)"
case "$OS" in
    Linux)  os="linux" ;;
    Darwin) os="osx" ;;
    *)
        echo "Error: Unsupported operating system: $OS" >&2
        exit 1
        ;;
esac

# Detect architecture
ARCH="$(uname -m)"
case "$ARCH" in
    x86_64|amd64)  arch="x64" ;;
    arm64|aarch64) arch="arm64" ;;
    *)
        echo "Error: Unsupported architecture: $ARCH" >&2
        exit 1
        ;;
esac

RID="${os}-${arch}"
ASSET_NAME="twig-${RID}.tar.gz"

# Verify curl is available
if ! command -v curl &>/dev/null; then
    echo "Error: curl is required but not found. Please install curl and try again." >&2
    exit 1
fi

# Query GitHub Releases API for latest release
RELEASE_URL="https://api.github.com/repos/${REPO}/releases/latest"

CURL_HEADERS=(-H "User-Agent: twig-installer")
if [ -n "${GITHUB_TOKEN:-}" ]; then
    CURL_HEADERS+=(-H "Authorization: Bearer $GITHUB_TOKEN")
fi

# Use -sSL (no -f) so the response body is captured even on HTTP 4xx errors
# Redirect stderr to /dev/null to suppress raw curl diagnostics on the terminal
# (the script provides its own clean error message via the CURL_EXIT check below)
CURL_EXIT=0
RELEASE_JSON="$(curl -sSL "${CURL_HEADERS[@]}" "$RELEASE_URL" 2>/dev/null)" || CURL_EXIT=$?

if [ "$CURL_EXIT" -ne 0 ]; then
    echo "Error: Failed to reach GitHub API (curl exit code $CURL_EXIT). Check your internet connection." >&2
    echo "  URL: $RELEASE_URL" >&2
    exit 1
fi

# Check for GitHub API rate limiting (HTTP 403 with rate-limit message in body)
if echo "$RELEASE_JSON" | grep -qi "API rate limit exceeded"; then
    if [ -n "${GITHUB_TOKEN:-}" ]; then
        echo "Error: GitHub API rate limit exceeded even with GITHUB_TOKEN. Try again later." >&2
    else
        echo "Error: GitHub API rate limit exceeded. Try again later or set the GITHUB_TOKEN environment variable to a GitHub personal access token." >&2
    fi
    exit 1
fi

# Check for empty or error response
if [ -z "$RELEASE_JSON" ]; then
    echo "Error: Failed to query GitHub Releases API. Received empty response." >&2
    echo "  URL: $RELEASE_URL" >&2
    exit 1
fi

# Extract download URL for matching asset
# Uses grep/sed to avoid requiring jq
DOWNLOAD_URL="$(echo "$RELEASE_JSON" | grep -o "\"browser_download_url\": *\"[^\"]*${ASSET_NAME}\"" | head -1 | sed 's/.*": *"\(.*\)"/\1/')"
if [ -z "$DOWNLOAD_URL" ]; then
    echo "Error: Asset '$ASSET_NAME' not found in the latest release." >&2
    echo "  This platform/architecture ($RID) may not be supported." >&2
    exit 1
fi

# Extract version tag for display
TAG_NAME="$(echo "$RELEASE_JSON" | grep -o '"tag_name": *"[^"]*"' | head -1 | sed 's/.*": *"\(.*\)"/\1/')"
echo "Downloading twig ${TAG_NAME} for ${RID}..."

# Download to temp
TEMP_DIR="$(mktemp -d)"
TEMP_ARCHIVE="${TEMP_DIR}/${ASSET_NAME}"
trap 'rm -rf "$TEMP_DIR"' EXIT

if ! curl -fsSL -o "$TEMP_ARCHIVE" "$DOWNLOAD_URL"; then
    echo "Error: Failed to download $DOWNLOAD_URL" >&2
    exit 1
fi

# Create install directory
mkdir -p "$INSTALL_DIR"

# Extract binary
if ! tar -xzf "$TEMP_ARCHIVE" -C "$INSTALL_DIR"; then
    echo "Error: Failed to extract archive." >&2
    exit 1
fi

# Verify binary exists
if [ ! -f "${INSTALL_DIR}/twig" ]; then
    echo "Error: twig binary not found after extraction." >&2
    echo "  The archive may have an unexpected structure." >&2
    exit 1
fi

# Make executable
chmod +x "${INSTALL_DIR}/twig"

# Verify companion binaries (warn only — older archives may not include them)
for companion in twig-mcp twig-tui; do
    if [ -f "${INSTALL_DIR}/${companion}" ]; then
        chmod +x "${INSTALL_DIR}/${companion}"
        echo "  Found $companion"
    else
        echo "Warning: $companion not found in archive. Some features may be unavailable. Run 'twig upgrade' after install to fetch companions." >&2
    fi
done

# Add to PATH via shell profile if not already present
PATH_EXPORT="export PATH=\"\$HOME/.twig/bin:\$PATH\""

add_to_profile() {
    local profile="$1"
    if [ -f "$profile" ] && grep -qF '.twig/bin' "$profile"; then
        echo "PATH already configured in $profile"
        return
    fi
    # Create or update the shell profile with twig PATH entry
    echo "" >> "$profile"
    echo "# Twig CLI" >> "$profile"
    echo "$PATH_EXPORT" >> "$profile"
    echo "Added twig to PATH in $profile"
}

# Detect shell and update appropriate profile(s)
SHELL_NAME="$(basename "${SHELL:-/bin/bash}")"
case "$SHELL_NAME" in
    bash)
        if [ -f "$HOME/.bashrc" ]; then
            add_to_profile "$HOME/.bashrc"
        fi
        if [ -f "$HOME/.bash_profile" ]; then
            add_to_profile "$HOME/.bash_profile"
        elif [ -f "$HOME/.profile" ]; then
            add_to_profile "$HOME/.profile"
        fi
        ;;
    zsh)
        add_to_profile "$HOME/.zshrc"
        ;;
    fish)
        FISH_CONFIG="$HOME/.config/fish/config.fish"
        if [ -f "$FISH_CONFIG" ] && grep -qF '.twig/bin' "$FISH_CONFIG"; then
            echo "PATH already configured in $FISH_CONFIG"
        else
            mkdir -p "$(dirname "$FISH_CONFIG")"
            echo "" >> "$FISH_CONFIG"
            echo "# Twig CLI" >> "$FISH_CONFIG"
            echo "fish_add_path \$HOME/.twig/bin" >> "$FISH_CONFIG"
            echo "Added twig to PATH in $FISH_CONFIG"
        fi
        ;;
    *)
        # Fallback: try common profiles
        if [ -f "$HOME/.profile" ]; then
            add_to_profile "$HOME/.profile"
        else
            echo "Warning: Could not detect shell profile. Add the following to your shell profile:" >&2
            echo "  $PATH_EXPORT" >&2
        fi
        ;;
esac

# Add to current session PATH
export PATH="$INSTALL_DIR:$PATH"

# Print version
echo ""
echo "twig installed successfully!"
if VERSION="$("${INSTALL_DIR}/twig" --version 2>&1)"; then
    echo "  $VERSION"
else
    echo "  (installed to ${INSTALL_DIR}/twig)"
fi
echo ""
echo "Restart your terminal or run the following to update PATH in your current session:"
echo "  export PATH=\"$INSTALL_DIR:\$PATH\""
