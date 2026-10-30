#!/usr/bin/env bash
# Fires concurrent requests from ONE client at the nginx-fronted cluster and shows:
#   - how many were allowed vs. rate limited (should equal the policy limit), and
#   - which replicas served them (should be spread across app1/app2/app3).
#
# Usage:  ./scripts/verify-cluster.sh
#         REQUESTS=60 ENDPOINT=/api/search EXPECTED_ALLOWED=20 ./scripts/verify-cluster.sh
#
# Works with the bash 3.2 that ships with macOS (no associative arrays).

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
ENDPOINT="${ENDPOINT:-/api/orders}"
REQUESTS="${REQUESTS:-30}"
EXPECTED_ALLOWED="${EXPECTED_ALLOWED:-10}"   # "orders" policy: Token Bucket, 10 per minute

# Fresh API key per run, so earlier runs don't eat into this run's quota.
API_KEY="verify-$(date +%s)-$RANDOM"

if ! curl -sf -o /dev/null "$BASE_URL/health"; then
    echo "Cluster not reachable at $BASE_URL. Start it with: docker compose up -d --build" >&2
    exit 1
fi

results="$(mktemp)"
trap 'rm -f "$results"' EXIT

echo "Sending $REQUESTS concurrent requests to $ENDPOINT as one client..."

for _ in $(seq 1 "$REQUESTS"); do
    (
        headers="$(curl -s -o /dev/null -D - -H "X-Api-Key: $API_KEY" "$BASE_URL$ENDPOINT" | tr -d '\r' || true)"
        status="$(printf '%s\n' "$headers" | awk 'NR == 1 { print $2 }')"
        served_by="$(printf '%s\n' "$headers" | awk -F': ' 'tolower($1) == "x-served-by" { print $2 }')"
        echo "${status:-ERR} ${served_by:-unknown}" >> "$results"
    ) &
done
wait

echo
echo "Status codes:"
awk '{ print $1 }' "$results" | sort | uniq -c

echo
echo "Requests handled per replica (all statuses):"
awk '{ print $2 }' "$results" | sort | uniq -c

echo
echo "Allowed (200) per replica:"
awk '$1 == "200" { print $2 }' "$results" | sort | uniq -c

allowed="$(awk '$1 == "200"' "$results" | wc -l | tr -d ' ')"
unexpected="$(awk '$1 != "200" && $1 != "429"' "$results" | wc -l | tr -d ' ')"
replicas="$(awk '{ print $2 }' "$results" | sort -u | grep -vc '^unknown$' || true)"

echo
# Anything other than 200 or 429 (a 502 from nginx, a 503, a dropped connection)
# means the cluster misbehaved, even if the allowed count happens to look right.
if [ "$unexpected" -gt 0 ]; then
    echo "FAIL: $unexpected response(s) were neither 200 nor 429 (see status codes above)." >&2
    exit 1
fi

if [ "$allowed" -eq "$EXPECTED_ALLOWED" ] && [ "$replicas" -ge 2 ]; then
    echo "PASS: $allowed allowed in total across $replicas replicas (limit $EXPECTED_ALLOWED), no unexpected statuses."
else
    echo "FAIL: $allowed allowed across $replicas replicas; expected exactly $EXPECTED_ALLOWED across 2+ replicas." >&2
    exit 1
fi
