# 10. Operating modes (debug / approval)

| Mode | Behavior |
|---|---|
| `DryRun` | Plan only. The plan is logged and shown in the UI; nothing is executed. |
| `ApproveAll` | Every plan waits in the **Approvals** window. User approves all / selected / rejects. |
| `Guarded` (default after first runs) | Safe ops (create folder, download new, upload new) run automatically; deletes, overwrites, moves and conflicts wait for approval. |
| `Auto` | Everything runs automatically; safety guards ([§9](09-safety-guards.md)) still apply. |

Default for a new pair: `ApproveAll`. The UI suggests switching to `Guarded`, then `Auto`, after successful cycles.

Additional debug controls:

- `MaxOpsPerCycle` (default unlimited; e.g. 20 for first runs) — execute only the first N action ops (base-only
  updates do not count), re-plan next cycle. Ops after the cut, including base-only ones, are dropped.
- If a phase-0 op (move, rename, file-vs-folder conflict, [§6.5](06-sync-algorithm.md)) is rejected or limited
  away, nothing after it is executed, because the rest of the plan was computed on the state after it.
- Union-mode plans (initial sync) always wait for approval, even in `Guarded`/`Auto` ([§7.2](07-scenarios.md)).
- Approval UI groups ops by type and folder, shows reason (`local modified`, `remote deleted`, …),
  sizes, and for overwrites both versions' size/mtime.
- **Rejected** ops: the item is put into the *held* list and is not proposed again until it changes
  on either side, or the user releases it. (Not in the prototype: rejected ops are simply proposed again.)
- "Approve and remember" rules: e.g. always auto-approve downloads into `Camera Uploads`.
- Every executed op is listed in the Activity log with an "Open"/"Show in folder" action.
- CLI: `wds plan <pair> [--full]` prints the plan; `wds sync <pair> --once [--dry-run] [--max-ops N]
  [--mode M] [--yes] [--accept-guards] [--full]`. The console asks `[y]es / [s]afe ops only / [N]o` for ops that
  need approval (`--yes` approves); a guard trip needs typing `yes` (or `--accept-guards`). Without an interactive
  console both default to "no". Exit codes: 0 done, 1 error, 2 partially done, 3 stopped by a guard, the user or
  root sanity.
