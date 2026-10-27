#!/usr/bin/env bash
# Run from a reviewed checkout of codex/sfu-server on the VPS, as root.
set -euo pipefail
if [[ $EUID -ne 0 ]]; then echo 'Run as root.' >&2; exit 1; fi
if [[ $(uname -m) != x86_64 ]]; then echo 'This installer targets Ubuntu amd64.' >&2; exit 1; fi
public_ip=${1:-89.126.195.20}
if ! [[ $public_ip =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then echo 'Expected public IPv4.' >&2; exit 1; fi
IFS=. read -r a b c d <<< "$public_ip"
for octet in "$a" "$b" "$c" "$d"; do if (( 10#$octet > 255 )); then echo 'Invalid IPv4.' >&2; exit 1; fi; done
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
apt-get update
apt-get install -y --no-install-recommends ca-certificates curl openssl tar
work=$(mktemp -d /tmp/wisp-sfu-build.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
# Pinned SDK + checksum from https://go.dev/dl/?mode=json; no unverified curl|sh.
go_version=1.27.3
go_sha=65efbc80fdc1fa2cd2631cad5b481777a68e8725f34e89f50fc6ac714e379b40
curl --fail --location --proto '=https' --tlsv1.2 "https://go.dev/dl/go${go_version}.linux-amd64.tar.gz" -o "$work/go.tar.gz"
printf '%s  %s\n' "$go_sha" "$work/go.tar.gz" | sha256sum --check --status
tar -xzf "$work/go.tar.gz" -C "$work"
(
 cd "$source_dir"
 export GOPATH="$work/gopath" GOCACHE="$work/cache" GOMAXPROCS=1 GOMEMLIMIT=512MiB CGO_ENABLED=0 GOTOOLCHAIN=local
 "$work/go/bin/go" mod verify
 "$work/go/bin/go" build -mod=readonly -trimpath -ldflags='-s -w' -o "$work/wisp-sfu" .
)
if ! getent passwd wisp-sfu >/dev/null; then useradd --system --user-group --no-create-home --shell /usr/sbin/nologin wisp-sfu; fi
install -d -o root -g wisp-sfu -m 0750 /etc/wisp-sfu
# Preserve certificate and pin on upgrades.
if [[ ! -e /etc/wisp-sfu/server.crt && ! -e /etc/wisp-sfu/server.key ]]; then
 openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -sha256 -nodes -days 365 \
  -subj "/CN=$public_ip" -addext "subjectAltName=IP:$public_ip" \
  -keyout /etc/wisp-sfu/server.key -out /etc/wisp-sfu/server.crt
fi
if [[ ! -f /etc/wisp-sfu/server.crt || ! -f /etc/wisp-sfu/server.key ]]; then echo 'Certificate/key pair incomplete; fix it without replacing the existing pin.' >&2; exit 1; fi
chown root:wisp-sfu /etc/wisp-sfu/server.key /etc/wisp-sfu/server.crt
chmod 0640 /etc/wisp-sfu/server.key /etc/wisp-sfu/server.crt
if [[ ! -e /etc/wisp-sfu/server.json ]]; then
 cat > /etc/wisp-sfu/server.json <<JSON
{
  "listen": ":8443",
  "publicIp": "$public_ip",
  "udpPort": 50000,
  "certificate": "/etc/wisp-sfu/server.crt",
  "privateKey": "/etc/wisp-sfu/server.key",
  "apiKey": "AIzaSyD1nnA4S5GjJmuK0g_mq96aGsrS70vncSg",
  "databaseUrl": "https://wisp-dd1ec-default-rtdb.asia-southeast1.firebasedatabase.app",
  "maxSessions": 5
}
JSON
fi
chown root:wisp-sfu /etc/wisp-sfu/server.json
chmod 0640 /etc/wisp-sfu/server.json
install -o root -g root -m 0755 "$work/wisp-sfu" /usr/local/bin/wisp-sfu.new
mv -f /usr/local/bin/wisp-sfu.new /usr/local/bin/wisp-sfu
install -o root -g root -m 0644 "$source_dir/deploy/wisp-sfu.service" /etc/systemd/system/wisp-sfu.service
install -d -o root -g root -m 0755 /usr/share/doc/wisp-sfu
install -o root -g root -m 0644 "$source_dir/THIRD_PARTY_NOTICES.txt" /usr/share/doc/wisp-sfu/THIRD_PARTY_NOTICES.txt
systemctl daemon-reload
systemctl enable wisp-sfu.service
systemctl restart wisp-sfu.service
systemctl --no-pager --full status wisp-sfu.service
# Use the certificate itself for this local health check; never disable TLS checks.
curl --fail --silent --show-error --retry 10 --retry-connrefused --retry-delay 1 --cacert /etc/wisp-sfu/server.crt --resolve "$public_ip:8443:127.0.0.1" "https://$public_ip:8443/healthz"
pin=$(openssl x509 -in /etc/wisp-sfu/server.crt -outform DER | sha256sum | cut -d ' ' -f 1)
printf '\nWisp settings (PUBLIC; safe to share):\nURL: wss://%s:8443/rtc\nSHA-256: %s\n' "$public_ip" "$pin"
printf '\nAllow 8443/tcp and 50000/udp in the VPS/provider firewall. SSH/firewall rules were not changed.\n'
