"""Validate the planning package and export its acceptance CSV.

This validates DOCUMENT structure/traceability, not application behavior.
"""
from __future__ import annotations

import csv
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def main() -> None:
    architecture = (ROOT / "ARCHITECTURE.md").read_text(encoding="utf-8")
    handoff = (ROOT / "LUNA-HANDOFF.md").read_text(encoding="utf-8")
    tests = json.loads((ROOT / "acceptance.json").read_text(encoding="utf-8"))
    ledger = json.loads((ROOT / "plan-sources.json").read_text(encoding="utf-8"))
    errors: list[str] = []
    requirements = re.findall(r"^\| (C\d{2}) \|", architecture, re.MULTILINE)
    req_set = set(requirements)
    if not req_set or len(requirements) != len(req_set):
        errors.append("Requirement register is empty or has duplicate IDs")
    phases = set(re.findall(r"^### (P\d+) —", architecture, re.MULTILINE))
    gates = re.findall(r"^\| (G\d{2}) \|", architecture, re.MULTILINE)
    if len(gates) != len(set(gates)):
        errors.append("Duplicate decision gate IDs")
    ids = [t["id"] for t in tests]
    if len(ids) != len(set(ids)):
        errors.append("Duplicate acceptance test IDs")
    covered: set[str] = set()
    for test in tests:
        for field in ("id", "requirements", "phase", "scenario", "exercise", "expected", "scope"):
            if not test.get(field):
                errors.append(f"{test.get('id')}: empty {field}")
        unknown = set(test["requirements"]) - req_set
        if unknown:
            errors.append(f"{test['id']}: unknown requirements {sorted(unknown)}")
        covered.update(test["requirements"])
        if test["phase"] not in phases:
            errors.append(f"{test['id']}: unknown phase {test['phase']}")
    if req_set - covered:
        errors.append(f"Uncovered requirements: {sorted(req_set - covered)}")
    prose = architecture.split("## Sources", 1)[0]
    citations = set(map(int, re.findall(r"\[(\d+)\]", prose)))
    ledger_ids = {s["id"] for s in ledger["sources"]}
    if citations - ledger_ids:
        errors.append(f"Unknown citations: {sorted(citations - ledger_ids)}")
    if "WhisperX is the leading integrated desktop" not in architecture:
        errors.append("Current WhisperX direction missing")
    if "GGML/GGUF files" not in architecture or "CTranslate2" not in architecture:
        errors.append("Model format boundary missing")
    if "have **not been run against an app**" not in architecture:
        errors.append("Unexecuted-app-test disclosure missing")
    if "not permission" not in handoff:
        errors.append("Handoff scope boundary missing")

    # CSV is generated from structured scenario data, not hand-counted prose.
    csv_path = ROOT / "ACCEPTANCE-MATRIX.csv"
    columns = ["id", "requirements", "phase", "scenario", "exercise", "expected", "scope", "status", "evidence"]
    with csv_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns)
        writer.writeheader()
        for test in tests:
            writer.writerow({**test, "requirements": "; ".join(test["requirements"]), "status": "NOT RUN — proposed application acceptance test", "evidence": ""})
    with csv_path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if len(rows) != len(tests) or [r["id"] for r in rows] != ids:
        errors.append("CSV round-trip mismatch")

    # Verify actual local document references; remote sources are citation-ledger checked.
    linked_files = re.findall(r"`([^`\n]+\.(?:md|csv|json))`", architecture + "\n" + handoff)
    package_names = {"ARCHITECTURE.md", "LUNA-HANDOFF.md", "ACCEPTANCE-MATRIX.csv", "DISCOVERY.md", "RESEARCH-MODELS.md"}
    for name in linked_files:
        if name in package_names and not (ROOT / name).is_file():
            errors.append(f"Missing referenced file: {name}")

    report = {
        "document_validation": "PASS" if not errors else "FAIL",
        "confirmed_requirements": len(req_set),
        "requirements_with_acceptance_coverage": len(covered & req_set),
        "proposed_application_tests": len(tests),
        "roadmap_phases": len(phases),
        "decision_gates": len(gates),
        "cited_sources": len(citations),
        "csv_round_trip_rows": len(rows),
        "application_tests_executed": 0,
        "model_benchmarks_executed": 0,
        "errors": errors,
    }
    (ROOT / "DOCUMENT-VALIDATION.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
