#!/usr/bin/env python3
"""Validate every example manifest and model file against the JSON Schemas.

CI runs this so the schemas and the strict C# parser can never drift apart
silently: an example accepted by one and rejected by the other fails the build.
"""

import json
import pathlib
import sys

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = pathlib.Path(__file__).resolve().parents[1]
SCHEMAS = ROOT / "schemas"


def load(p: pathlib.Path):
    return json.loads(p.read_text())


def main() -> int:
    registry = Registry()
    for schema_path in SCHEMAS.glob("*.schema.json"):
        schema = load(schema_path)
        resource = Resource.from_contents(schema)
        registry = registry.with_resource(schema["$id"], resource)
        # Also register by bare filename for relative $refs.
        registry = registry.with_resource(schema_path.name, resource)

    validators = {
        "tspi-scenario/1": Draft202012Validator(load(SCHEMAS / "scenario.v1.schema.json"), registry=registry),
        "tspi-addendum/1": Draft202012Validator(load(SCHEMAS / "addendum.v1.schema.json"), registry=registry),
        "tspi-model/1": Draft202012Validator(load(SCHEMAS / "model.v1.schema.json"), registry=registry),
    }

    failures = 0
    checked = 0
    targets = sorted((SCHEMAS / "examples").glob("*.json")) + sorted((ROOT / "models").glob("*.json"))
    for path in targets:
        doc = load(path)
        schema_id = doc.get("schema")
        v = validators.get(schema_id)
        if v is None:
            print(f"FAIL {path.relative_to(ROOT)}: unknown or missing 'schema' field: {schema_id!r}")
            failures += 1
            continue
        errors = sorted(v.iter_errors(doc), key=lambda e: e.json_path)
        checked += 1
        if errors:
            failures += 1
            print(f"FAIL {path.relative_to(ROOT)}:")
            for e in errors[:5]:
                print(f"  {e.json_path}: {e.message}")
        else:
            print(f"ok   {path.relative_to(ROOT)}")

    print(f"\n{checked} files checked, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
