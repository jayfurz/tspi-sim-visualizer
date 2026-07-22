#!/usr/bin/env bash
# End-to-end exercise of the whole pipeline. Run from the repo root.
set -euo pipefail
cd "$(dirname "$0")/.."

TSPI="dotnet src/artifacts/bin/Tspi.Cli/Debug/net8.0/tspi.dll"
PYTHON="${PYTHON:-python3}"   # point at a venv python that has tspi-py + jsonschema installed
WORK=$(mktemp -d /tmp/tspi-e2e.XXXXXX)
trap 'jobs -p | xargs -r kill 2> /dev/null; rm -rf "$WORK"' EXIT

step() { printf '\n\033[1;36m== %s ==\033[0m\n' "$*"; }

step "build + unit/V&V/golden tests"
(cd src && dotnet build Tspi.sln -v q && dotnet test Tspi.Tests/Tspi.Tests.csproj -v q --nologo)

step "validate manifests"
$TSPI validate schemas/examples/minimal.json
$TSPI validate schemas/examples/intercept.json
$TSPI validate schemas/examples/all-maneuvers.json
$TSPI validate schemas/examples/nn-intercept.json
$TSPI validate schemas/examples/ship-to-air.json

step "ship-to-air reference engagement (VLS launch kick)"
$TSPI run schemas/examples/ship-to-air.json -o "$WORK/ship.tspi" | grep -E "intercept|cpa"

step "run intercept scenario"
$TSPI run schemas/examples/intercept.json -o "$WORK/run.tspi"

step "determinism: second run is byte-identical"
$TSPI run schemas/examples/intercept.json -o "$WORK/run2.tspi" --quiet
$TSPI diff "$WORK/run.tspi" "$WORK/run2.tspi"

step "learned (nn) guidance: run + weights hash in provenance"
$TSPI run schemas/examples/nn-intercept.json -o "$WORK/nn.tspi" | grep -E "cpa|intercept"
$TSPI inspect "$WORK/nn.tspi" --provenance | grep -F "generic-nn-losrate" > /dev/null && echo "policy hash in provenance ok"

step "measured-TSPI import: export -> reimport -> positions identical"
$TSPI export "$WORK/run.tspi" --format csv -o "$WORK/measured.csv"
$TSPI import "$WORK/measured.csv" --origin 34.9061,-117.8839,700 -o "$WORK/imported.tspi"
$TSPI diff "$WORK/run.tspi" "$WORK/imported.tspi" --tol-m 1e-9

step "simulated munition vs imported (measured) tracks"
$TSPI append "$WORK/imported.tspi" schemas/examples/addendum-late-munition.json
$TSPI inspect "$WORK/imported.tspi" --provenance | grep -F '"op":"import"' > /dev/null && echo "import provenance ok"

step "append red counter-missile against recorded tracks"
$TSPI append "$WORK/run.tspi" schemas/examples/addendum-late-munition.json
$TSPI inspect "$WORK/run.tspi" --chain | tail -4

step "torn-append recovery"
cp "$WORK/run.tspi" "$WORK/torn.tspi"
"$PYTHON" - "$WORK/torn.tspi" <<'EOF'
import os, sys
p = sys.argv[1]
os.truncate(p, os.path.getsize(p) - 33)
EOF
$TSPI recover "$WORK/torn.tspi" --apply
$TSPI inspect "$WORK/torn.tspi" | grep -E "entities \("

step "csv export"
$TSPI export "$WORK/run.tspi" --format csv -o "$WORK/run.csv"
wc -l "$WORK/run.csv"

step "monte carlo sweep (50 seeds)"
$TSPI sweep schemas/examples/intercept.json --seeds 1:50 --out-dir "$WORK/sweep" --quiet
wc -l "$WORK/sweep/index.jsonl"

step "tspi serve: viewer + run/validate endpoints"
$TSPI serve --port 18321 --root "$WORK" --out-dir "$WORK/runs" > /dev/null 2>&1 &
sleep 1
curl -sf http://127.0.0.1:18321/ | grep -q app.js
curl -sf -X POST --data-binary @schemas/examples/intercept.json \
  http://127.0.0.1:18321/api/validate | grep -q '"valid": true'
SERVE_FILE=$(curl -sf -X POST --data-binary @schemas/examples/intercept.json \
  http://127.0.0.1:18321/api/run | "$PYTHON" -c 'import json,sys;print(json.load(sys.stdin)["file"])')
# Fetch to disk, then check the magic: `curl | head -c 4` would die of SIGPIPE on the
# left of `&&`, where set -e ignores it — the check would silently not check.
curl -sf "http://127.0.0.1:18321$SERVE_FILE" -o "$WORK/served.tspi"
head -c 4 "$WORK/served.tspi" | grep -q TSPI
echo "serve ok"
kill %% 2> /dev/null

step "json schema conformance"
"$PYTHON" scripts/check_schemas.py > /dev/null && echo "schemas ok"

step "python reader over the run + golden contract tests"
"$PYTHON" -m pytest tools/tspi_py/tests/ -q
"$PYTHON" - "$WORK/run.tspi" <<'EOF'
import sys
from tspi_py import TspiFile
f = TspiFile(sys.argv[1])
assert len(f.entities) == 4, f.entities.keys()
aam = f.samples("blue-01-aam-1")
print(f"python read: {f} | aam peak speed {max((aam['vel']**2).sum(axis=1))**0.5:.0f} m/s")
EOF

printf '\n\033[1;32mE2E PASSED\033[0m\n'
