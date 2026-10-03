#!/bin/sh
# Turns the Jenkins env file (the same .env style the SIRISTUDIOPHOTO jobs use) into what SIRIAUTOPOST.Api needs.
#   sh deploy/prepare-env.sh <env-file> <api-env-out> <postgres-env-out>
# api-env-out:      only the keys the API reads, so the other apps' secrets in the same file never reach this pod.
# postgres-env-out: POSTGRES_USER/POSTGRES_PASSWORD when the file has them (in-cluster Postgres), else empty.
# Prints key names and the database host only, never values.
set -eu

SRC="$1"
API_OUT="$2"
PG_OUT="$3"
DB_NAME_DEFAULT=SIRIAUTOPOST_PRD

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT
# A file saved on Windows may carry a UTF-8 BOM and CRLF line ends.
sed -e '1s/^\xEF\xBB\xBF//' -e 's/\r$//' "$SRC" > "$TMP"

getv() { grep -E "^$1=" "$TMP" | tail -n 1 | cut -d= -f2- || true; }

: > "$API_OUT"
: > "$PG_OUT"
grep -E '^(ConnectionStrings|Jwt|Admin|Cors|Database)__[A-Za-z0-9_]+=' "$TMP" >> "$API_OUT" || true

db_name="$(getv SIRIAUTOPOST_DB_NAME)"
db_name="${db_name:-$DB_NAME_DEFAULT}"

# SIRIAUTOPOST_PG_USER / SIRIAUTOPOST_PG_PASSWORD (from the Jenkinsfile, kept in the postgres-env secret) select the
# in-cluster Postgres and win over the env file.
pg_user="${SIRIAUTOPOST_PG_USER:-$(getv POSTGRES_USER)}"
pg_pass="${SIRIAUTOPOST_PG_PASSWORD:-$(getv POSTGRES_PASSWORD)}"
if [ -n "$pg_user" ] && [ -n "$pg_pass" ]; then
    printf 'POSTGRES_USER=%s\nPOSTGRES_PASSWORD=%s\n' "$pg_user" "$pg_pass" > "$PG_OUT"
fi

# Connection string: ConnectionStrings__Default as is; otherwise the shared server from AppSettings__ConnectionStrings
# (SIRISTUDIOPHOTO style) with this app's own database; otherwise the in-cluster Postgres.
if ! grep -q '^ConnectionStrings__Default=.' "$API_OUT"; then
    cs=""
    if [ -s "$PG_OUT" ]; then
        cs="Host=postgres;Database=$db_name;Username=$pg_user;Password=$pg_pass"
    else
        cs="$(getv AppSettings__ConnectionStrings)"
    fi
    if [ -z "$cs" ]; then
        echo "env file needs ConnectionStrings__Default, AppSettings__ConnectionStrings or POSTGRES_USER + POSTGRES_PASSWORD" >&2
        exit 1
    fi
    if printf '%s' "$cs" | grep -qiE '(^|;)[[:space:]]*(Database|Initial Catalog)[[:space:]]*='; then
        cs="$(printf '%s' "$cs" | sed -E "s/(^|;)[[:space:]]*(Database|Initial Catalog)[[:space:]]*=[^;]*/\\1Database=$db_name/I")"
    else
        cs="${cs%;};Database=$db_name"
    fi
    # The aspnet image has no GSSAPI library.
    printf '%s' "$cs" | grep -qi 'Gss Encryption Mode' || cs="${cs%;};Gss Encryption Mode=Disable"
    sed -i '/^ConnectionStrings__Default=/d' "$API_OUT"
    echo "ConnectionStrings__Default=$cs" >> "$API_OUT"
fi

# JWT signing key (32+ characters). Derived from AppSettings__Secret when not given, so this app's tokens are never
# valid for the app that owns that secret and vice versa.
if ! grep -qE '^Jwt__Key=.{32,}' "$API_OUT"; then
    secret="$(getv AppSettings__Secret)"
    if [ -z "$secret" ]; then
        echo "env file needs Jwt__Key (32+ characters) or AppSettings__Secret" >&2
        exit 1
    fi
    key="$(printf '%s:siriautopost-jwt' "$secret" | sha256sum | cut -d' ' -f1)"
    sed -i '/^Jwt__Key=/d' "$API_OUT"
    echo "Jwt__Key=$key" >> "$API_OUT"
fi

echo "api env keys: $(cut -d= -f1 "$API_OUT" | sort -u | tr '\n' ' ')"
echo "database: $(grep '^ConnectionStrings__Default=' "$API_OUT" | grep -oiE '(Host|Server)=[^;]*' | head -n 1) / $db_name"
if [ -s "$PG_OUT" ]; then echo "postgres: in-cluster StatefulSet"; else echo "postgres: external server"; fi
