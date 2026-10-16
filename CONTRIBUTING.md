# Contributing

Thank you for your interest. A few ground rules, written to keep this project maintainable by a single part-time maintainer.

## Scope and expectations

- **No SLA.** Issues and pull requests are reviewed when time allows.
- **Supported platform**: Revit 2026, Windows 11. Other Revit versions are added only when an issue with a reproducible case asks for them.
- **No external LLM calls** inside RIFT. Learning features (tool suggestions, rule proposals) are produced locally and always require a human to review and activate them.
- **The add-in depends on nothing but Revit.** `addin/` is a standalone .NET `IExternalApplication`; it does not load, import, reference or require any other Revit add-in (ADR-011), and its dependencies are isolated in their own `AssemblyLoadContext` (ADR-021). Build rules: .NET SDK 8, `dotnet build addin/rift.sln -c "Release R26"`, warnings are errors, banned APIs (process spawning, networking, console) fail the build. Scripts executed by the add-in are Python 3.4 as implemented by IronPython 3.4 (ADR-016); the server lint is the judge. See `addin/README.md`.

## Privacy — read this before any commit

This repository is public. Never commit:
- model files (`*.rvt`, `*.rte`, `*.rfa`), Revit journals, dumps, discovery files, `.key` files, `runs/`, `captures/`, `learning/`;
- real project names, client names, internal identifiers, absolute paths containing a user name, machine names, screenshots showing a title bar or a title block;
- test fixtures extracted from a real model — fixtures are generated from a fictitious project created by the test itself.

CI runs `gitleaks` plus generic patterns; the maintainer runs an additional private term check. Install the hooks: `pre-commit install && pre-commit install --hook-type pre-push`.

## Workflow

1. Open an issue first for anything bigger than a typo. Bug reports must include the output of `rift-mcp doctor` (redacted) and, when relevant, a `run_id`.
2. Branch from `main` (`feat/…`, `fix/…`, `bench/…`). `main` is protected; everything lands through a pull request, even from the maintainer.
3. Add a line under `Unreleased` in `CHANGELOG.md` (Keep a Changelog). CI fails without it unless the PR carries the `no-changelog` label.
4. Tests: server code is tested with `pytest` against the fake Revit in `server/tests/fake_revit`; add-in code that does not need Revit is unit-tested with xUnit in `addin/tests`; behaviour inside Revit is covered by the self-test ribbon button, run manually and recorded in the release PR.
5. Files adapted from bimwright/rvt-mcp keep the attribution header required by Apache-2.0 §4.

## Decisions

Architecture decisions are recorded in `docs/DECISIONS.md` (ADR-NNN). Propose a change by opening an issue that references the ADR you want to revisit.

## Publication checklist (maintainer)

Run before the first public push and before every release:
1. `git log --all -p | grep -E "$PRIVATE_TERMS"` is empty (history counts, not only HEAD).
2. No fixture comes from a real model; `tests/` only contains generated data.
3. `docs/` screenshots are cropped (no title bar, no title block); example project is fictitious; no absolute path.
4. `pyproject.toml` authors = pseudonym + dedicated e-mail; no employer homepage.
5. `git log --format=%ae | sort -u` shows only the pseudonym address.
6. `.gitignore` covers `runs/`, `captures/`, `learning/`, `*.rvt`, `*.rte`, `*.dmp`, `journal.*.txt`, `private-terms.txt`, `export-allowlist.txt`, local `policy.json`.
7. `SECURITY.md`, `LICENSE`, `NOTICE`, `THIRD_PARTY.md`, `CODEOWNERS`, trademark disclaimer present.
8. Every `skills/*/SKILL.md` and `rules/*.yaml` re-read by a human (richest in internal context).
9. Dependabot, CodeQL and private vulnerability reporting enabled.
10. Clone the public repository into an empty folder, run `gitleaks detect` and the private regex again.
