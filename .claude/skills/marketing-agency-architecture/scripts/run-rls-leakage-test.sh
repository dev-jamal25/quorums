#!/usr/bin/env bash
# run-rls-leakage-test.sh
#
# Runs the two-brand RLS leakage gate (DL-002 / DL-007). This MUST pass before any
# feature work proceeds past Day 2 of the build order.
#
# The test (backend/tests/IntegrationTests/RlsLeakageTests.cs) uses Testcontainers
# to spin up a disposable pgvector Postgres, so the only host requirements are the
# .NET SDK and a working Docker daemon.
#
# Usage:
#   scripts/run-rls-leakage-test.sh
#
# Exit code is non-zero if isolation is not proven.

set -euo pipefail

# Resolve repo root relative to this script (script lives in <skill>/scripts).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Adjust if your solution lives elsewhere.
TEST_PROJECT="${TEST_PROJECT:-backend/tests/IntegrationTests}"
FILTER="${FILTER:-FullyQualifiedName~RlsLeakageTests}"

echo "==> Preconditions"
command -v dotnet >/dev/null 2>&1 || { echo "ERROR: dotnet SDK not found on PATH."; exit 2; }
docker info >/dev/null 2>&1 || { echo "ERROR: Docker daemon not reachable (Testcontainers needs it)."; exit 2; }

echo "==> Running RLS leakage gate: ${FILTER}"
echo "    Project: ${TEST_PROJECT}"

dotnet test "${TEST_PROJECT}" \
  --filter "${FILTER}" \
  --logger "console;verbosity=normal"

echo "==> RLS leakage gate PASSED — brand isolation proven by the database, not a WHERE clause."
