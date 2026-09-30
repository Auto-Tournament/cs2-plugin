#!/bin/bash
set -e

# Colors for output
GREEN="\033[0;32m"
BLUE="\033[0;34m"
YELLOW="\033[1;33m"
RED="\033[0;31m"
NC="\033[0m" # No Color

# Source .env if present so DISCORD_WEBHOOK_URL and others are available
if [ -f ".env" ]; then
    echo -e "${BLUE}Sourcing .env file...${NC}"
    set -a
    # shellcheck source=/dev/null
    source ".env"
    set +a
fi

echo -e "${BLUE}🚀 MatchZy Enhanced Automated Release Script${NC}\n"

ensure_cmd() {
    local cmd="$1"
    local help="$2"
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo -e "${RED}❌ Missing required command: ${cmd}${NC}"
        echo -e "${YELLOW}${help}${NC}"
        return 1
    fi
    return 0
}

install_deps_debian_like() {
    # Best-effort dependency installer for Debian/Ubuntu.
    # Requires root or passwordless sudo (sudo -n).
    local pkgs=("$@")

    if [ "${#pkgs[@]}" -eq 0 ]; then
        return 0
    fi

    if [ "$(id -u)" -eq 0 ]; then
        apt-get update
        DEBIAN_FRONTEND=noninteractive apt-get install -y "${pkgs[@]}"
        return 0
    fi

    if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
        sudo apt-get update
        sudo DEBIAN_FRONTEND=noninteractive apt-get install -y "${pkgs[@]}"
        return 0
    fi

    echo -e "${RED}❌ Cannot auto-install packages (need root or passwordless sudo).${NC}"
    echo -e "${YELLOW}Run this manually, then re-run ./release.sh:${NC}"
    echo "  sudo apt-get update && sudo apt-get install -y ${pkgs[*]}"
    exit 1
}

install_dotnet8_debian12() {
    # Installs .NET 8 SDK on Debian 12 (bookworm) via Microsoft packages repo.
    # Requires root or passwordless sudo (sudo -n).
    local needs_dotnet_install=0
    if command -v dotnet >/dev/null 2>&1; then
        return 0
    fi

    needs_dotnet_install=1
    if [ "$needs_dotnet_install" -eq 1 ]; then
        echo -e "${BLUE}📦 Installing dotnet-sdk-8.0 (Debian 12)...${NC}"
        install_deps_debian_like ca-certificates wget gnupg

        if [ "$(id -u)" -eq 0 ]; then
            wget -q "https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb" -O /tmp/packages-microsoft-prod.deb
            dpkg -i /tmp/packages-microsoft-prod.deb
            apt-get update
            DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-8.0
            return 0
        fi

        if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
            wget -q "https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb" -O /tmp/packages-microsoft-prod.deb
            sudo dpkg -i /tmp/packages-microsoft-prod.deb
            sudo apt-get update
            sudo DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-8.0
            return 0
        fi

        echo -e "${RED}❌ Cannot auto-install dotnet (need root or passwordless sudo).${NC}"
        echo -e "${YELLOW}Install it manually, then re-run ./release.sh.${NC}"
        echo "  sudo apt-get update && sudo apt-get install -y ca-certificates wget gnupg"
        echo "  wget -q \"https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb\" -O /tmp/packages-microsoft-prod.deb"
        echo "  sudo dpkg -i /tmp/packages-microsoft-prod.deb"
        echo "  sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0"
        exit 1
    fi
}

preflight_deps() {
    # Ensure we have the tooling needed BEFORE we modify files/commit/push.
    local missing=0

    ensure_cmd git "Install git first (e.g. Debian/Ubuntu: sudo apt-get install -y git)" || missing=1

    # dotnet (we can auto-install on Debian 12; otherwise print a hint)
    if ! command -v dotnet >/dev/null 2>&1; then
        # Attempt auto-install on Debian 12 if apt exists.
        if [ -f /etc/os-release ] && grep -q 'VERSION_ID="12"' /etc/os-release && grep -q 'ID=debian' /etc/os-release && command -v apt-get >/dev/null 2>&1; then
            install_dotnet8_debian12
        else
            echo -e "${RED}❌ Missing required command: dotnet${NC}"
            echo -e "${YELLOW}Install .NET 8 SDK, then re-run ./release.sh (https://learn.microsoft.com/dotnet/core/install/)${NC}"
            missing=1
        fi
    fi

    # zip + gh (both are apt-installable on Debian/Ubuntu)
    if ! command -v zip >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then
            echo -e "${BLUE}📦 Installing zip...${NC}"
            install_deps_debian_like zip
        else
            echo -e "${RED}❌ Missing required command: zip${NC}"
            echo -e "${YELLOW}Install zip then re-run (e.g. Debian/Ubuntu: sudo apt-get install -y zip).${NC}"
            missing=1
        fi
    fi

    if ! command -v gh >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then
            echo -e "${BLUE}📦 Installing GitHub CLI (gh)...${NC}"
            install_deps_debian_like gh
        else
            echo -e "${RED}❌ Missing required command: gh${NC}"
            echo -e "${YELLOW}Install GitHub CLI then re-run (https://cli.github.com/).${NC}"
            missing=1
        fi
    fi

    if [ "$missing" -ne 0 ]; then
        exit 1
    fi
}

preflight_auth() {
    # Verify GitHub auth is ready BEFORE we modify files / commit / push.
    # - gh must be logged in (used for release creation)
    # - git must be able to push to origin (release commit + tags)

    echo -e "\n${BLUE}🔐 Verifying GitHub authentication...${NC}"

    if ! gh auth status -h github.com >/dev/null 2>&1; then
        echo -e "${RED}❌ GitHub CLI is not authenticated.${NC}"
        echo -e "${YELLOW}Run:${NC} gh auth login"
        echo -e "${YELLOW}Then (recommended for HTTPS remotes):${NC} gh auth setup-git"
        exit 1
    fi

    # Show which GitHub user gh is using (helps when multiple accounts exist).
    local gh_user
    gh_user=$(gh api user -q .login 2>/dev/null || echo "")
    if [ -n "$gh_user" ]; then
        echo -e "${GREEN}✓ gh authenticated as: ${gh_user}${NC}"
    fi

    # Ensure gh token can access this repo.
    # Prefer letting gh infer the repo from the current git remote (avoids parsing bugs).
    REPO_SLUG="$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null || echo "")"
    if [ -z "$REPO_SLUG" ]; then
        # Fallback: parse from origin URL.
        local repo_url repo_slug
        repo_url=$(git remote get-url origin 2>/dev/null || echo "")
        repo_slug=""
        if [[ "$repo_url" =~ ^git@github\.com: ]]; then
            repo_slug="${repo_url#git@github.com:}"
        elif [[ "$repo_url" =~ ^https?://github\.com/ ]]; then
            repo_slug="${repo_url#https://github.com/}"
            repo_slug="${repo_slug#http://github.com/}"
        fi
        repo_slug="${repo_slug%.git}"

        if [ -n "$repo_slug" ]; then
            if gh repo view "$repo_slug" --json nameWithOwner -q .nameWithOwner >/dev/null 2>&1; then
                REPO_SLUG="$repo_slug"
            fi
        fi
    fi

    if [ -z "$REPO_SLUG" ]; then
        echo -e "${RED}❌ gh is authenticated but cannot access the GitHub repo for this directory.${NC}"
        echo -e "${YELLOW}Fix:${NC}"
        echo "  - Ensure this is a GitHub repo with a valid origin remote"
        echo "  - Ensure you're logged into gh as an account with access to the repo"
        echo "    (try: gh auth login --hostname github.com)"
        echo "  - Or refresh scopes: gh auth refresh -h github.com -s repo"
        echo "  - If using a PAT via environment, set GH_TOKEN to a token with repo access"
        exit 1
    fi
    echo -e "${GREEN}✓ gh repo access OK: ${REPO_SLUG}${NC}"

    # Verify git push authentication without making changes.
    # Disable interactive prompts so the script fails fast in CI/servers.
    if ! GIT_TERMINAL_PROMPT=0 git push --dry-run origin HEAD >/dev/null 2>&1; then
        echo -e "${RED}❌ Git is not able to authenticate to push to 'origin'.${NC}"
        echo -e "${YELLOW}If your origin remote is HTTPS, the easiest fix is:${NC}"
        echo "  gh auth setup-git"
        echo -e "${YELLOW}If it still fails, you likely have cached bad HTTPS credentials. Clear them, then retry:${NC}"
        echo "  git credential reject <<'EOF'"
        echo "  protocol=https"
        echo "  host=github.com"
        echo "  EOF"
        echo -e "${YELLOW}Alternatively use SSH for the origin remote and ensure your SSH key is added to GitHub.${NC}"
        exit 1
    fi

    echo -e "${GREEN}✓ GitHub authentication looks good (gh + git push).${NC}"
}

preflight_release_config() {
    # Release-script configuration preflight.
    #
    # By default we require Discord webhook configuration so releases always
    # get announced (avoids accidentally forgetting it).
    #
    # To explicitly skip Discord announcements, set:
    #   SKIP_DISCORD_WEBHOOK=1

    if [ "${SKIP_DISCORD_WEBHOOK:-}" = "1" ] || [ "${SKIP_DISCORD_WEBHOOK:-}" = "true" ]; then
        echo -e "${YELLOW}⚠️  SKIP_DISCORD_WEBHOOK is set; Discord notification will be skipped.${NC}"
        return 0
    fi

    if [ -z "${DISCORD_WEBHOOK_URL:-}" ]; then
        echo -e "${RED}❌ DISCORD_WEBHOOK_URL is required for releases but is not set.${NC}"
        echo -e "${YELLOW}Fix one of these:${NC}"
        echo "  - Create a .env file in this repo (see .env.example), or"
        echo "  - Export DISCORD_WEBHOOK_URL in your shell"
        echo -e "${YELLOW}Or to explicitly skip Discord notification:${NC}"
        echo "  SKIP_DISCORD_WEBHOOK=1 ./release.sh ${BUMP_TYPE:-}"
        exit 1
    fi

    if ! command -v curl >/dev/null 2>&1; then
        echo -e "${RED}❌ Missing required command: curl (needed for Discord webhook).${NC}"
        echo -e "${YELLOW}Install curl, then re-run (e.g. Debian/Ubuntu: sudo apt-get install -y curl).${NC}"
        exit 1
    fi

    if [ ! -f "./discord-webhook.sh" ]; then
        echo -e "${RED}❌ discord-webhook.sh not found; cannot send Discord release notification.${NC}"
        echo -e "${YELLOW}If you truly want to proceed without Discord notification:${NC}"
        echo "  SKIP_DISCORD_WEBHOOK=1 ./release.sh ${BUMP_TYPE:-}"
        exit 1
    fi
}

# Get current version from AutoTournamentCS2.cs
CURRENT_VERSION=$(grep "ModuleVersion =>" src/AutoTournamentCS2.cs | sed -E "s/.*\"(.*)\".*/\1/")
if [ -z "$CURRENT_VERSION" ]; then
    echo -e "${RED}❌ Could not detect version from AutoTournamentCS2.cs${NC}"
    exit 1
fi

# Parse version components
IFS=. read -r MAJOR MINOR PATCH <<< "$CURRENT_VERSION"

# Check for version bump argument or explicit version
BUMP_TYPE="${1:-none}"
VERSION="$CURRENT_VERSION"

# If the first argument looks like X.Y.Z, treat it as an explicit version override
if [[ "$BUMP_TYPE" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    VERSION="$BUMP_TYPE"
    BUMP_TYPE="explicit"
    echo -e "${YELLOW}🔧 Using explicitly specified version: ${CURRENT_VERSION} → ${VERSION}${NC}"
elif [ "$BUMP_TYPE" = "major" ]; then
    MAJOR=$((MAJOR + 1))
    MINOR=0
    PATCH=0
    VERSION="${MAJOR}.${MINOR}.${PATCH}"
    echo -e "${YELLOW}🔼 Bumping MAJOR version: ${CURRENT_VERSION} → ${VERSION}${NC}"
elif [ "$BUMP_TYPE" = "minor" ]; then
    MINOR=$((MINOR + 1))
    PATCH=0
    VERSION="${MAJOR}.${MINOR}.${PATCH}"
    echo -e "${YELLOW}🔼 Bumping MINOR version: ${CURRENT_VERSION} → ${VERSION}${NC}"
elif [ "$BUMP_TYPE" = "patch" ]; then
    PATCH=$((PATCH + 1))
    VERSION="${MAJOR}.${MINOR}.${PATCH}"
    echo -e "${YELLOW}🔼 Bumping PATCH version: ${CURRENT_VERSION} → ${VERSION}${NC}"
elif [ "$BUMP_TYPE" != "none" ]; then
    echo -e "${RED}❌ Invalid bump type or version: ${BUMP_TYPE}${NC}"
    echo "Usage: ./release.sh [major|minor|patch|X.Y.Z]"
    echo "  major: 0.8.15 → 1.0.0"
    echo "  minor: 0.8.15 → 0.9.0"
    echo "  patch: 0.8.15 → 0.8.16"
    echo "  X.Y.Z: explicitly set version (e.g. 1.0.6)"
    echo "  (no arg): Use current version"
    exit 1
else
    echo -e "${GREEN}📦 Using current version: ${VERSION}${NC}"
fi

# This is the 2.x line (dev). 1.x hotfixes are released from hotfix/1.4.x; guard against
# releasing a 1.x build as 2.x, or releasing from a detached HEAD (nothing to push).
if [ "${VERSION%%.*}" = "1" ]; then
    echo -e "${RED}❌ dev only releases 2.x+ versions (got ${VERSION}). 1.x is released from hotfix/1.4.x.${NC}"
    exit 1
fi
if [ -z "$(git branch --show-current 2>/dev/null)" ]; then
    echo -e "${RED}❌ Not on a branch (detached HEAD). Run the release on a proper branch.${NC}"
    exit 1
fi

# Ensure working tree is completely clean before proceeding
if ! git diff --quiet || ! git diff --cached --quiet; then
    echo -e "${RED}❌ Working tree is not clean. Please commit or stash all changes before running the release script.${NC}"
    echo "Tip: run 'git status' to see pending changes."
    exit 1
fi

# Final confirmation before doing anything destructive
echo -e "\n${YELLOW}You are about to run a release for version v${VERSION}.${NC}"
echo -e "${YELLOW}This will clean builds, optionally update AutoTournamentCS2.cs, commit, push, and create a GitHub release + tag.${NC}"
CURRENT_BRANCH=$(git branch --show-current 2>/dev/null || echo "unknown-branch")
echo -e "${YELLOW}Current Git branch: ${CURRENT_BRANCH}${NC}"
read -rp "$(echo -e \"${YELLOW}Continue with release v${VERSION}? [y/N]: ${NC}\")" CONFIRM_RELEASE
case "$CONFIRM_RELEASE" in
    y|Y|yes|YES)
        echo -e "${GREEN}Proceeding with release v${VERSION}...${NC}"
        ;;
    *)
        echo -e "${RED}Aborting release v${VERSION}. No changes were made.${NC}"
        exit 0
        ;;
esac

# Ensure tooling + authentication exists before we do anything else.
preflight_deps
preflight_auth
preflight_release_config

# Check if tag already exists (local or on origin)
if git rev-parse "v${VERSION}" >/dev/null 2>&1 || git ls-remote --exit-code --tags origin "refs/tags/v${VERSION}" >/dev/null 2>&1; then
    echo -e "${RED}❌ Tag v${VERSION} already exists!${NC}"
    echo "Please bump to a new version or delete the existing tag:"
    echo "  git tag -d v${VERSION}"
    echo "  git push origin :refs/tags/v${VERSION}"
    exit 1
fi

# Update version in AutoTournamentCS2.cs BEFORE building (so the build includes the new version)
if [ "$BUMP_TYPE" != "none" ]; then
    echo -e "\n${BLUE}📝 Updating version in AutoTournamentCS2.cs to ${VERSION}...${NC}"
    # Use different sed syntax for Linux vs macOS
    if [[ "$OSTYPE" == "darwin"* ]]; then
        sed -i "" "s/ModuleVersion => \\\".*\\\"/ModuleVersion => \\\"${VERSION}\\\"/" src/AutoTournamentCS2.cs
    else
        sed -i "s/ModuleVersion => \\\".*\\\"/ModuleVersion => \\\"${VERSION}\\\"/" src/AutoTournamentCS2.cs
    fi
    echo -e "${GREEN}✓ Updated AutoTournamentCS2.cs to version ${VERSION}${NC}"
fi

# Clean previous builds
echo -e "\n${BLUE}🧹 Cleaning previous builds...${NC}"
rm -rf build/ bin/ obj/

# Restore dependencies
echo -e "\n${BLUE}📥 Restoring dependencies...${NC}"
dotnet restore

# Build and publish (now with the updated version)
echo -e "\n${BLUE}🔨 Building project (Release mode)...${NC}"
dotnet publish -c Release

# Create release directory structure under build/
RELEASE_DIR="AutoTournamentCS2-${VERSION}"
BUILD_ROOT="build"
rm -rf "${BUILD_ROOT}/${RELEASE_DIR}" "${BUILD_ROOT}/${RELEASE_DIR}.zip"
mkdir -p "${BUILD_ROOT}/${RELEASE_DIR}/addons/counterstrikesharp/plugins/AutoTournamentCS2"
mkdir -p "${BUILD_ROOT}/${RELEASE_DIR}/cfg/AutoTournamentCS2"

# Copy plugin files to proper directory structure
echo -e "\n${BLUE}📂 Creating directory structure...${NC}"
cp -r build/Release/net8.0/publish/* "${BUILD_ROOT}/${RELEASE_DIR}/addons/counterstrikesharp/plugins/AutoTournamentCS2/"

# Copy config files
echo -e "${BLUE}📂 Copying config files...${NC}"
cp -r cfg/AutoTournamentCS2/* "${BUILD_ROOT}/${RELEASE_DIR}/cfg/AutoTournamentCS2/"
# database.json holds the operator's database connection. The plugin writes a default one on
# first start when it is missing, so the zip leaves it out: extracting an update (or the 2.0.0
# rename, which carries the old database.json over only when the new folder has none) must
# never replace a MySQL setup with the SQLite default.
rm -f "${BUILD_ROOT}/${RELEASE_DIR}/cfg/AutoTournamentCS2/database.json"

# Create zip file
echo -e "\n${BLUE}🗜️  Creating release archive...${NC}"
mkdir -p "${BUILD_ROOT}"
(
  # Zip the contents, not the folder: addons/ and cfg/ sit at the root of the archive, so the
  # plugin is at addons/counterstrikesharp/plugins/AutoTournamentCS2/AutoTournamentCS2.dll.
  cd "${BUILD_ROOT}/${RELEASE_DIR}" && zip -r -q "../${RELEASE_DIR}.zip" addons cfg
)

# The layout csm and operators rely on. Fail before anything is committed or published.
ZIP_LISTING="$(unzip -Z1 "${BUILD_ROOT}/${RELEASE_DIR}.zip")"
for required in \
    "addons/counterstrikesharp/plugins/AutoTournamentCS2/AutoTournamentCS2.dll" \
    "cfg/AutoTournamentCS2/config.cfg"; do
    if ! grep -qx "$required" <<< "$ZIP_LISTING"; then
        echo -e "${RED}❌ ${RELEASE_DIR}.zip is missing ${required}${NC}"
        exit 1
    fi
done
if grep -q "/database.json$" <<< "$ZIP_LISTING"; then
    echo -e "${RED}❌ ${RELEASE_DIR}.zip must not contain database.json${NC}"
    exit 1
fi

# Get file size for display
SIZE=$(du -h "${BUILD_ROOT}/${RELEASE_DIR}.zip" | cut -f1)
echo -e "${GREEN}✓ Created ${BUILD_ROOT}/${RELEASE_DIR}.zip (${SIZE})${NC}"

# Set default repo for gh CLI (if not already set)
if [ -n "${REPO_SLUG:-}" ]; then
    gh repo set-default "${REPO_SLUG}" 2>/dev/null || true
else
    REPO_URL=$(git remote get-url origin | sed -E "s|.*github.com[:/](.*)\\.git|\\1|" | sed -E "s|\\.git$||")
    gh repo set-default "$REPO_URL" 2>/dev/null || true
fi

echo -e "\n${BLUE}💾 Committing changes...${NC}"
git add .
git commit -m "Release v${VERSION}"

echo -e "\n${BLUE}📝 Generating changelog for GitHub release...${NC}"

# Changelog based on commits between the previous release on this branch (if any) and HEAD.
# Only tags reachable from HEAD count: a tag from another line (e.g. a 1.x hotfix) is not on this branch.
prev_tag=$(git tag --merged HEAD --sort=-v:refname | head -n 1 || echo "")

log_range=""
if [ -n "$prev_tag" ]; then
    log_range="${prev_tag}..HEAD"
else
    # First tag in the repo – use all reachable commits
    log_range="HEAD"
fi

CHANGELOG=$(git log ${log_range} --pretty=format:"- %s" 2>/dev/null | grep -viE "^- Release v[0-9]+\.[0-9]+\.[0-9]+$" || true)
if [ -z "$CHANGELOG" ] || [ ${#CHANGELOG} -lt 10 ]; then
    CHANGELOG="- Release v${VERSION}"
fi

RELEASE_NOTES=$(cat <<EOF
## Changelog

${CHANGELOG}

## Installation

1. Download \`${RELEASE_DIR}.zip\`
2. Extract the contents to your CS2 server game/csgo/ directory
   - The zip contains the proper folder structure (\`addons/\` and \`cfg/\`)
3. Restart your server

## Requirements

- CounterStrikeSharp (latest version)
- CS2 Dedicated Server

## Configuration

Config files are located in \`csgo/cfg/AutoTournamentCS2/\`:
- \`config.cfg\` - Main plugin configuration
- \`admins.json\` - Admin permissions
- \`database.json\` - Database settings
- \`live.cfg\`, \`warmup.cfg\`, \`knife.cfg\` - Match configs
EOF
)

# Push release commit (but no tag yet). This ensures the commit exists on GitHub
# before we ask GitHub to create a tag/release pointing at it.
echo -e "\n${BLUE}⬆️  Pushing release commit to origin...${NC}"
CURRENT_BRANCH=$(git branch --show-current)
git push origin "$CURRENT_BRANCH"
RELEASE_SHA=$(git rev-parse HEAD)

# Create GitHub release (this will also create tag vX.Y.Z on GitHub if it doesn't exist)
echo -e "\n${BLUE}🌟 Creating GitHub release (and tag v${VERSION})...${NC}"
# --target: without it GitHub creates the tag on the default branch (dev), not on this commit.
PRERELEASE="${PRERELEASE:-false}"
LATEST_OR_PRERELEASE_FLAG="--latest"
if [ "$PRERELEASE" = "true" ] || [ "$PRERELEASE" = "1" ]; then
    echo -e "${YELLOW}⚠️  Publishing as a pre-release; \"latest\" will not be touched.${NC}"
    LATEST_OR_PRERELEASE_FLAG="--prerelease"
fi
gh release create "v${VERSION}" \
    "${BUILD_ROOT}/${RELEASE_DIR}.zip" \
    --target "${RELEASE_SHA}" \
    --title "MatchZy Enhanced v${VERSION}" \
    --notes "$RELEASE_NOTES" \
    --draft=false \
    ${LATEST_OR_PRERELEASE_FLAG}

# Send Discord webhook notification (required unless explicitly skipped)
if [ "${SKIP_DISCORD_WEBHOOK:-}" = "1" ] || [ "${SKIP_DISCORD_WEBHOOK:-}" = "true" ]; then
    echo -e "\n${YELLOW}⚠️  SKIP_DISCORD_WEBHOOK is set; skipping Discord release notification.${NC}"
else
    echo -e "\n${BLUE}🔔 Sending Discord release notification...${NC}"
    # Run via bash so the script doesn't need +x.
    bash ./discord-webhook.sh "${VERSION}"
fi

# Cleanup
echo -e "\n${BLUE}🧹 Cleaning up temporary files...${NC}"
rm -rf "${BUILD_ROOT:?}/${RELEASE_DIR}"

echo -e "\n${GREEN}✅ Release v${VERSION} published successfully!${NC}"
echo -e "${GREEN}🔗 View release: https://github.com/${REPO_URL}/releases/tag/v${VERSION}${NC}"
