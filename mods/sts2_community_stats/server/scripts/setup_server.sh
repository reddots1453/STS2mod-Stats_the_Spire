#!/usr/bin/env bash
# ============================================================
#  STS2 Community Stats — Server Setup Script
#  Target: Ubuntu 22.04 / 24.04 / Debian 12+
#  Run as root (or with sudo) from /opt/sts2stats/
# ============================================================

set -euo pipefail

DOMAIN="${1:-api.sts2stats.com}"
EMAIL="${2:-admin@sts2stats.com}"
DB_PASSWORD="${3:-$(openssl rand -hex 16)}"

echo "=========================================="
echo "  STS2 Community Stats Server Setup"
echo "  Domain: ${DOMAIN}"
echo "  Email:  ${EMAIL}"
echo "=========================================="

# ── 1. System packages ──────────────────────────────────────
echo "[1/7] Installing system packages..."

# Docker's `docker-compose-plugin` is NOT in Ubuntu's default apt repos —
# it only exists in Docker's official APT repository. We must add that
# repo first, then install `docker-ce` + `docker-compose-plugin` together.
# Ubuntu's legacy `docker.io` package is intentionally removed to avoid
# a split installation (old daemon + new CLI plugins).

echo "  Installing prerequisites..."
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg lsb-release \
    nginx certbot python3-certbot-nginx \
    jq htop > /dev/null

# Remove any legacy Docker packages that would conflict with docker-ce.
# `|| true` because these may not be installed on a fresh VPS.
echo "  Removing legacy docker packages (if any)..."
apt-get remove -y -qq \
    docker docker-engine docker.io containerd runc \
    docker-doc docker-compose podman-docker > /dev/null 2>&1 || true

# Add Docker's official GPG key + APT source (official instructions).
if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
    echo "  Adding Docker official APT repository..."
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
        | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    chmod a+r /etc/apt/keyrings/docker.gpg

    # VERSION_CODENAME is defined by /etc/os-release on Ubuntu/Debian.
    . /etc/os-release
    ARCH=$(dpkg --print-architecture)
    echo "deb [arch=${ARCH} signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
        > /etc/apt/sources.list.d/docker.list
fi

apt-get update -qq
echo "  Installing docker-ce + compose plugin..."
apt-get install -y -qq \
    docker-ce docker-ce-cli containerd.io \
    docker-buildx-plugin docker-compose-plugin > /dev/null

# Sanity check: both must be present before continuing.
docker --version
docker compose version

# Enable Docker
systemctl enable --now docker

# ── 2. Create project directory ─────────────────────────────
echo "[2/7] Setting up project directory..."
PROJECT_DIR="/opt/sts2stats"
mkdir -p "${PROJECT_DIR}"
cd "${PROJECT_DIR}"

# Copy server files (assumes this script is run from the server/ directory
# after scp/rsync the server folder to the VPS)
if [ ! -f docker-compose.yml ]; then
    echo "ERROR: docker-compose.yml not found in ${PROJECT_DIR}"
    echo "Please copy the server/ directory here first:"
    echo "  rsync -avz server/ root@your-server:/opt/sts2stats/"
    exit 1
fi

# ── 3. Environment file ────────────────────────────────────
echo "[3/7] Creating environment file..."
cat > .env << EOF
DB_PASSWORD=${DB_PASSWORD}
EOF
chmod 600 .env
echo "  DB_PASSWORD saved to .env"

# ── 4. SSL certificate ─────────────────────────────────────
echo "[4/7] Obtaining SSL certificate..."

# Temporary Nginx config for ACME challenge
cat > /etc/nginx/conf.d/acme.conf << NGINX
server {
    listen 80;
    server_name ${DOMAIN};
    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }
    location / {
        return 444;
    }
}
NGINX
mkdir -p /var/www/certbot
systemctl restart nginx

certbot certonly --webroot -w /var/www/certbot \
    -d "${DOMAIN}" --non-interactive --agree-tos -m "${EMAIL}" || {
    echo "WARNING: certbot failed. You may need to set up DNS first."
    echo "After DNS is ready, run: certbot certonly --webroot -w /var/www/certbot -d ${DOMAIN}"
}

# Clean up temp Nginx config (Docker Nginx will take over)
rm -f /etc/nginx/conf.d/acme.conf
systemctl stop nginx
systemctl disable nginx  # Docker handles Nginx

# Update Nginx config with actual domain
sed -i "s/api.sts2stats.com/${DOMAIN}/g" nginx/default.conf

# ── 5. Start services ──────────────────────────────────────
echo "[5/7] Starting Docker services..."
docker compose up -d --build

echo "  Waiting for services to be healthy..."
sleep 10

# ── 6. Verify health ───────────────────────────────────────
echo "[6/7] Health check..."
HEALTH=$(curl -sf http://localhost:8080/health || echo '{"status":"unreachable"}')
echo "  API health: ${HEALTH}"

DB_HEALTH=$(docker compose exec -T db pg_isready -U sts2stats -d sts2stats 2>/dev/null && echo "ok" || echo "fail")
echo "  Database: ${DB_HEALTH}"

REDIS_HEALTH=$(docker compose exec -T redis redis-cli ping 2>/dev/null || echo "fail")
echo "  Redis: ${REDIS_HEALTH}"

# ── 7. Set up maintenance cron ──────────────────────────────
echo "[7/7] Setting up maintenance cron jobs..."
CRON_FILE="/etc/cron.d/sts2stats"
cat > "${CRON_FILE}" << CRON
# STS2 Community Stats maintenance
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# Database maintenance (weekly, Sunday 3AM)
0 3 * * 0 root cd ${PROJECT_DIR} && docker compose exec -T db psql -U sts2stats -d sts2stats -f /docker-entrypoint-initdb.d/002_maintenance.sql >> /var/log/sts2stats-maint.log 2>&1

# Database backup (daily, 4AM)
0 4 * * * root mkdir -p /backups/sts2stats && docker compose exec -T db pg_dump -U sts2stats -Fc sts2stats > /backups/sts2stats/sts2stats_\$(date +\%Y\%m\%d).dump 2>> /var/log/sts2stats-backup.log

# Purge old backups (keep 30 days)
0 5 * * * root find /backups/sts2stats -name "*.dump" -mtime +30 -delete

# Docker log cleanup (weekly)
0 2 * * 0 root docker system prune -f >> /var/log/sts2stats-docker.log 2>&1
CRON
chmod 644 "${CRON_FILE}"

# ── Done ────────────────────────────────────────────────────
echo ""
echo "=========================================="
echo "  Setup complete!"
echo ""
echo "  API endpoint: https://${DOMAIN}/v1"
echo "  Health check: https://${DOMAIN}/health"
echo ""
echo "  DB Password:  ${DB_PASSWORD}"
echo "  (saved in ${PROJECT_DIR}/.env)"
echo ""
echo "  Useful commands:"
echo "    cd ${PROJECT_DIR}"
echo "    docker compose logs -f api    # API logs"
echo "    docker compose logs -f db     # Database logs"
echo "    docker compose ps             # Service status"
echo "    docker compose restart api    # Restart API"
echo "=========================================="
