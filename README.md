# Architecture draft package

Start with **ARCHITECTURE.md** for the full design and roadmap. **LUNA-HANDOFF.md** is the shorter entry point for an implementation agent.

## Contents

- `ARCHITECTURE.md` — product boundary, confirmed requirement register, recommended decisions, component/data contracts, WhisperX integration, recorder/media behavior, correction and timing rules, storage/recovery, privacy, platform gates, roadmap and sources.
- `LUNA-HANDOFF.md` — implementation priorities, non-negotiable behaviors, authority boundaries and phase completion expectations.
- `ACCEPTANCE-MATRIX.csv` — proposed application verification scenarios, all explicitly NOT RUN. Open in a spreadsheet or read as text; fields include requirement IDs, phase, exercise, expected result, status and evidence.
- `acceptance.json` — structured source for that matrix.
- `plan-sources.json` — source URL/ID ledger for architecture citations. It validates attribution identities; it is not a benchmark or a blanket proof of every engineering recommendation.
- `validate_documents.py` — standard-library Python validator and CSV generator.
- `DOCUMENT-VALIDATION.json` — latest document-structure/coverage result, not an application test report.
- `PACKAGE-MANIFEST.json` — packaged file sizes and SHA-256 digests for integrity checking.

## Current decision

WhisperX is the leading desktop pipeline candidate, pending code-switched corpus, packaging and performance tests. App-owned text/time/speaker/project contracts remain independent of the model backend. Avalonia is the first UI candidate to prove, not an approved irreversible dependency choice.

This is a design artifact. No app implementation, model inference benchmark, installation, account linkage or publication was performed to produce it. Creating this draft does not authorize those actions.

## Recheck document structure

With a normal Python installation, run from this folder:

```text
python validate_documents.py
```

This checks requirement/test/phase references, the source ledger IDs, documented scope disclosures, referenced companion files and CSV round-trip. It updates `ACCEPTANCE-MATRIX.csv` and `DOCUMENT-VALIDATION.json`; it does not execute the proposed application tests. Integrity hashes in the original package manifest describe the delivered files and are not automatically updated after local edits.

For human review, the most consequential sections are Architecture 2 (decisions), 5 (WhisperX), 7 (corrections), 9 (capture), 15 (roadmap), and 16.3 (open gates).
