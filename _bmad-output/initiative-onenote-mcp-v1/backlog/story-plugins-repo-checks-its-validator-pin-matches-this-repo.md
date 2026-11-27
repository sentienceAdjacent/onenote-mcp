---
id: 25
type: story
title: "Plugins repo checks its validator pin matches this repo"
parent: none
covers: [R7]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# Plugins repo checks its validator pin matches this repo

## Description

This repo's CI and the catalog repo `sentienceAdjacent/plugins` each pin the Claude Code version that runs `claude plugin validate` (2.1.318 today, in its `ci.yml` and `sync.yml`). Only comments keep the pins in step. Phill moved the check to the plugins repo (D32), and it was never added. This story adds a check in `sentienceAdjacent/plugins` CI that fails when its pin differs from this repo's, reading this repo's pins from its main branch (`.github/workflows/ci.yml` and `release.yml`, which must also agree with each other). The repo is public, so no token is needed.

## Acceptance Criteria

Verify: in `sentienceAdjacent/plugins`, a CI run fails when its validator pin differs from onenote-mcp's and passes when they match.

## Boundaries

- The work lands in `sentienceAdjacent/plugins` (local clone `D:\Development\sentienceAdjacent-plugins`), through its own PR.
- No change to this repo's release pipeline.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D13, D32)
- `sentienceAdjacent/plugins` — `.github/workflows/ci.yml` and `sync.yml`

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).

