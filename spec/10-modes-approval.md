# 10. Operating modes (debug / approval)

| Mode | Behavior |
|---|---|
| `DryRun` | Plan only. The plan is logged and shown in the UI; nothing is executed. |
| `ApproveAll` | Every plan waits in the **Approvals** window. User approves all / selected / rejects. |
| `Guarded` (default after first runs) | Safe ops (create folder, download new, upload new) run automatically; deletes, overwrites, moves and conflicts wait for approval. |
| `Auto` | Everything runs automatically; safety guards ([§9](09-safety-guards.md)) still apply. |

Default for a new pair: `ApproveAll`. The UI suggests switching to `Guarded`, then `Auto`, after successful cycles.

Additional debug controls:

- `MaxOpsPerCycle` (default unlimited; e.g. 20 for first runs) — execute only the first N ops, re-plan next cycle.
- Approval UI groups ops by type and folder, shows reason (`local modified`, `remote deleted`, …),
  sizes, and for overwrites both versions' size/mtime.
- **Rejected** ops: the item is put into the *held* list and is not proposed again until it changes
  on either side, or the user releases it.
- "Approve and remember" rules: e.g. always auto-approve downloads into `Camera Uploads`.
- Every executed op is listed in the Activity log with an "Open"/"Show in folder" action.
- CLI: `wds plan --pair <id>` prints the plan; `wds sync --once --dry-run`.
