#!/usr/bin/env bash
# End-to-end exercise of the whole pipeline. Run from the repo root.
set -euo pipefail
cd "$(dirname "$0")/.."

TSPI="dotnet src/artifacts/bin/Tspi.Cli/Debug/net10.0/tspi.dll"
PYTHON="${PYTHON:-python3}"   # point at a venv python that has tspi-py + jsonschema installed
WORK=$(mktemp -d /tmp/tspi-e2e.XXXXXX)
trap 'rm -rf "$WORK"' EXIT

step() { printf '\n\033[1;36m== %s ==\033[0m\n' "$*"; }

step "build + unit/V&V/golden tests"
(cd src && dotnet build Tspi.slnx -v q && dotnet test Tspi.Tests/Tspi.Tests.csproj -v q --nologo)

step "validate manifests"
$TSPI validate schemas/examples/minimal.json
$TSPI validate schemas/examples/intercept.json
$TSPI validate schemas/examples/all-maneuvers.json

step "run intercept scenario"
$TSPI run schemas/examples/intercept.json -o "$WORK/run.tspi"

step "determinism: second run is byte-identical"
$TSPI run schemas/examples/intercept.json -o "$WORK/run2.tspi" --quiet
$TSPI diff "$WORK/run.tspi" "$WORK/run2.tspi"

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
