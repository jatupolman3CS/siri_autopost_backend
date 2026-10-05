#!/bin/sh
# Run on the k8s host where cloudflared runs (tunnel `siri-monitor`, locally managed). Makes BOTH hostnames reachable:
#   siriautopost.siristudiophoto.com      -> http://172.17.0.1:30907  (PRD, namespace siriautopost)
#   siriautopost-dev.siristudiophoto.com  -> http://172.17.0.1:30908  (DEV, namespace siriautopost-dev)
# 1) DNS: a proxied CNAME per host -> <tunnel-id>.cfargotunnel.com (zone siristudiophoto.com)
# 2) ingress rules in the cloudflared config file, before the catch-all
# Usage: sh cloudflare-dns.sh [/etc/cloudflared/config.yml]
set -eu

CONFIG="${1:-/etc/cloudflared/config.yml}"

cloudflared tunnel route dns siri-monitor siriautopost.siristudiophoto.com
cloudflared tunnel route dns siri-monitor siriautopost-dev.siristudiophoto.com

add_rule() { # hostname port
    if grep -q "hostname: $1\$" "$CONFIG"; then echo "rule exists: $1"; return; fi
    # insert before the catch-all (`- service: ...` without hostname)
    awk -v h="$1" -v p="$2" '
        !done && /^[[:space:]]*-[[:space:]]*service:/ {
            print "  - hostname: " h; print "    service: http://172.17.0.1:" p; done=1 }
        { print }' "$CONFIG" > "$CONFIG.new"
    cp "$CONFIG" "$CONFIG.bak"
    mv "$CONFIG.new" "$CONFIG"
    echo "rule added: $1 -> $2"
}
add_rule siriautopost.siristudiophoto.com 30907
add_rule siriautopost-dev.siristudiophoto.com 30908

cloudflared tunnel --config "$CONFIG" ingress validate
echo "restart cloudflared (e.g. systemctl restart cloudflared), then:"
echo "  curl -fsS https://siriautopost.siristudiophoto.com/ && curl -fsS https://siriautopost-dev.siristudiophoto.com/"
