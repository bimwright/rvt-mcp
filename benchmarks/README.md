# Haiku benchmark — bimwright weak-model regression check

This folder holds the benchmark procedure and run history for detecting weak-model (Haiku 4.5) parameter-accuracy drift as the MCP tool surface evolves.

## When to run

Run the benchmark before merging when your PR:

- Adds five or more new handler files, **or**
- Edits any tool's description text, **or**
- Precedes tagging a minor or major release (`v0.X.0` for X ≥ 2).

Not required for: internal refactors that do not change the MCP surface, bug fixes that do not touch descriptions or parameter names, patch releases.

## How to run

1. Open Claude Code in this repo.
2. Load `benchmarks/template.md` — either `/run benchmarks/template.md` or paste its contents into the chat.
3. Claude will spawn a Haiku sub-agent via the `Agent` tool, feed it the 10 canonical queries with the current tool surface, score the results, and write a new file to `runs/<YYYY-MM-DD>-<commit-short>-<version>.md`.
4. Expect 10–15 minutes wall-clock. Uses your existing Claude Code subscription — no Anthropic API key needed.

## Threshold policy

Compare param-accuracy delta vs the most recent run in `runs/`:

| Δ vs last baseline | Reaction |
|---|---|
| < 5% | Within Haiku variance — ignore. |
| 5% – 15% | Flag in the PR description; merge allowed. |
| ≥ 15% | Block merge. Investigate the specific query failures — usually a description edit or tool-name collision is the culprit. |

Thresholds are reviewer conventions, not enforced CI gates.

## Baseline

`runs/2026-04-16-fc99c67-v0.1.0-baseline.md` is the reference all future runs compare against. It is derived from an internal brainstorm benchmark (18-tool RICH-description result block). Rebaseline only at major releases.
