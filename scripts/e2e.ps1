# End-to-end exercise of the whole pipeline — PowerShell port of e2e.sh for
# Windows (also runs under pwsh on Linux/macOS). Run from anywhere:
#   pwsh scripts/e2e.ps1 [-Python python]
param(
    # Point at a python that has tspi-py + jsonschema installed (e.g. a venv's).
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$TspiDll = "src/artifacts/bin/Tspi.Cli/Debug/net8.0/tspi.dll"
function tspi { dotnet $TspiDll @args; if ($LASTEXITCODE -ne 0) { throw "tspi $($args -join ' ') failed ($LASTEXITCODE)" } }
function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Assert([bool]$cond, [string]$what) { if (-not $cond) { throw "FAILED: $what" } }

$Work = Join-Path ([IO.Path]::GetTempPath()) ("tspi-e2e-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $Work | Out-Null
$Serve = $null
try {
    Step "build + unit/V&V/golden tests"
    dotnet build src/Tspi.sln -v q;  if ($LASTEXITCODE -ne 0) { throw "build failed" }
    dotnet test src/Tspi.Tests/Tspi.Tests.csproj -v q --nologo; if ($LASTEXITCODE -ne 0) { throw "tests failed" }

    Step "validate manifests"
    tspi validate schemas/examples/minimal.json
    tspi validate schemas/examples/intercept.json
    tspi validate schemas/examples/all-maneuvers.json
    tspi validate schemas/examples/nn-intercept.json
    tspi validate schemas/examples/ship-to-air.json
    tspi validate schemas/examples/ship-defense.json

    Step "ship-to-air reference engagement (VLS launch kick)"
    $out = tspi run schemas/examples/ship-to-air.json -o "$Work/ship.tspi"
    Assert ([bool]($out | Select-String "intercept|cpa")) "ship-to-air produced no endgame event"

    Step "ship defense: unparented red threat appears, blue SAM kills it"
    $out = tspi run schemas/examples/ship-defense.json -o "$Work/defense.tspi"
    Assert ([bool]($out | Select-String "intercept")) "SAM did not intercept the threat"
    Assert ([bool]($out | Select-String "killed")) "no killed event for the intercepted threat"

    Step "run intercept scenario"
    tspi run schemas/examples/intercept.json -o "$Work/run.tspi"

    Step "determinism: second run is byte-identical"
    tspi run schemas/examples/intercept.json -o "$Work/run2.tspi" --quiet
    tspi diff "$Work/run.tspi" "$Work/run2.tspi"

    Step "learned (nn) guidance: run + weights hash in provenance"
    $out = tspi run schemas/examples/nn-intercept.json -o "$Work/nn.tspi"
    Assert ([bool]($out | Select-String "cpa|intercept")) "nn run produced no endgame event"
    $out = tspi inspect "$Work/nn.tspi" --provenance
    Assert ([bool]($out | Select-String "generic-nn-losrate")) "policy hash missing from provenance"
    Write-Host "policy hash in provenance ok"

    Step "measured-TSPI import: export -> reimport -> positions identical"
    tspi export "$Work/run.tspi" --format csv -o "$Work/measured.csv"
    # NB: the quotes matter — bare 34.9061,-117.8839,700 is an ARRAY to PowerShell.
    tspi import "$Work/measured.csv" --origin "34.9061,-117.8839,700" -o "$Work/imported.tspi"
    tspi diff "$Work/run.tspi" "$Work/imported.tspi" --tol-m 1e-9

    Step "simulated munition vs imported (measured) tracks"
    tspi append "$Work/imported.tspi" schemas/examples/addendum-late-munition.json
    $out = tspi inspect "$Work/imported.tspi" --provenance
    Assert ([bool]($out | Select-String '"op":"import"')) "import provenance missing"
    Write-Host "import provenance ok"

    Step "append red counter-missile against recorded tracks"
    tspi append "$Work/run.tspi" schemas/examples/addendum-late-munition.json
    tspi inspect "$Work/run.tspi" --chain | Select-Object -Last 4

    Step "torn-append recovery"
    Copy-Item "$Work/run.tspi" "$Work/torn.tspi"
    $fs = [IO.File]::Open("$Work/torn.tspi", 'Open', 'ReadWrite')
    $fs.SetLength($fs.Length - 33)
    $fs.Close()
    tspi recover "$Work/torn.tspi" --apply
    $out = tspi inspect "$Work/torn.tspi"
    Assert ([bool]($out | Select-String "entities \(")) "recovered file does not inspect"

    Step "csv export"
    tspi export "$Work/run.tspi" --format csv -o "$Work/run.csv"
    Write-Host "$((Get-Content "$Work/run.csv").Count) lines $Work/run.csv"

    Step "monte carlo sweep (50 seeds)"
    tspi sweep schemas/examples/intercept.json --seeds 1:50 --out-dir "$Work/sweep" --quiet
    Write-Host "$((Get-Content "$Work/sweep/index.jsonl").Count) lines $Work/sweep/index.jsonl"

    Step "tspi serve: viewer + run/validate endpoints"
    $Serve = Start-Process dotnet -ArgumentList $TspiDll, 'serve', '--port', '18331', '--root', $Work, '--out-dir', "$Work/runs" `
        -RedirectStandardOutput "$Work/serve.log" -RedirectStandardError "$Work/serve.err" -PassThru
    Start-Sleep -Seconds 2
    $index = Invoke-WebRequest -Uri "http://127.0.0.1:18331/" -UseBasicParsing
    Assert ($index.Content -match "app\.js") "served index.html is not the viewer"
    $manifest = Get-Content -Raw schemas/examples/intercept.json
    $v = Invoke-RestMethod -Uri "http://127.0.0.1:18331/api/validate" -Method Post -Body $manifest
    Assert ($v.valid -eq $true) "/api/validate rejected the intercept example"
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:18331/api/run" -Method Post -Body $manifest
    Assert ($r.ok -eq $true) "/api/run failed"
    Invoke-WebRequest -Uri "http://127.0.0.1:18331$($r.file)" -OutFile "$Work/served.tspi" -UseBasicParsing
    $magic = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes("$Work/served.tspi")[0..3])
    Assert ($magic -eq "TSPI") "served run has bad magic '$magic'"
    Write-Host "serve ok"

    Step "json schema conformance"
    & $Python scripts/check_schemas.py | Out-Null; if ($LASTEXITCODE -ne 0) { throw "schema check failed" }
    Write-Host "schemas ok"

    Step "python reader over the run + golden contract tests"
    & $Python -m pytest tools/tspi_py/tests/ -q; if ($LASTEXITCODE -ne 0) { throw "pytest failed" }
    & $Python scripts/walkthrough_analysis.py "$Work/ship.tspi"; if ($LASTEXITCODE -ne 0) { throw "reader script failed" }

    Write-Host "`nE2E PASSED" -ForegroundColor Green
}
finally {
    if ($Serve -and -not $Serve.HasExited) { Stop-Process -Id $Serve.Id -Force }
    Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
}
