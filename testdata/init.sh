#!/usr/bin/env bash
# Loads the rehearsal database into SQL Server (used by docker-compose db-init, or run directly).
# Env: SQL_HOST (default localhost), MSSQL_SA_PASSWORD (required).
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
SQLCMD=""
for c in "$(command -v sqlcmd 2>/dev/null || true)" /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd; do
  if [ -n "$c" ] && [ -x "$c" ]; then SQLCMD="$c"; break; fi
done
if [ -z "$SQLCMD" ]; then echo "sqlcmd not found" >&2; exit 1; fi
HOST="${SQL_HOST:-localhost}"
echo "Using $SQLCMD against $HOST"
for f in "$HERE"/sql/*.sql; do
  echo ">> $(basename "$f")"
  "$SQLCMD" -C -b -f 65001 -S "$HOST" -U sa -P "$MSSQL_SA_PASSWORD" -i "$f"
done
echo "EpiRehearsal loaded. Check every row in the 99_verify output shows ok = 1."
