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
  on either side, or the user releases it. A held entry is `(key, op type)` plus the item's state on both sides
  (local size + mtime, remote size + etag, or mtime without an etag; `dir`; absent). Each plan drops the ops that
  match a held entry with the same state, and the action ops below a held folder op (their parent is not there);
  held ops are left out before the guards, so they do not count as mass deletes. Entries whose op is gone or whose
  state changed are released, but only on complete snapshots of both sides (an unlisted folder is not a change).
  Phase-0 ops (moves, renames) cannot be held: the rest of the plan depends on them, so rejecting one stops the
  cycle and it is proposed again. Held ops are excluded from the plan, so they do not prevent the first full cycle
  (§7.6): a held item has no base entry or keeps its old one, so it is never deleted because of being held.
- "Approve and remember" rules (`autoApprove`, [§15.5](15-config-storage.md)): op type + folder (whole subtree,
  empty = the whole pair) + `overwrite` (also uploads/downloads that replace an item; off by default). A matching op
  needs no approval in any mode; moves must stay inside the folder; a `Download` rule also covers `MkdirLocal` and an
  `Upload` rule `MkdirRemote` in its folder (the folders the transfers need). Rules never bypass guard trips (§9) or the
  union-mode approval (§7.2), e.g. always auto-approve downloads into `Camera Uploads`.
- Background service (tray, §8): each cycle executes the ops that need no approval right away and keeps the rest as
  the pending approval (replaced by the next cycle). Approving executes the chosen ops of that plan (preconditions are
  re-checked per op, like the CLI prompt); a pending plan older than 30 min is re-planned instead. A guard trip puts
  every action op into the pending approval and needs an explicit confirmation. Rejecting puts the ops into the held
  list. `DryRun` executes nothing; the plan is shown as pending.
- Every executed op is listed in the Activity log with an "Open"/"Show in folder" action.
- CLI: `wds plan <pair> [--full]` prints the plan; `wds sync <pair> --once [--dry-run] [--max-ops N]
  [--mode M] [--yes] [--accept-guards] [--full]`; both take `--keep-local <path>` / `--keep-remote <path>`
  ([§6.4](06-sync-algorithm.md)) and `--hold <path>` (hold the subtree's ops before guards and approval). The console
  asks `[y]es / [s]afe ops only / [h]old them, run the rest / [N]o` for ops that need approval (`--yes` approves); a guard trip needs typing `yes` (or `--accept-guards`). Keys typed before a prompt
  appears are discarded, so type-ahead never answers it. Without an interactive
  console both default to "no". `wds held <pair> [--release <path>] [--all]` lists or releases held ops;
  `wds rule list|add|remove <pair>` manages the auto-approve rules (`add --type Download --folder "Camera Uploads"
  [--overwrite]`). Exit codes: 0 done, 1 error, 2 partially done, 3 stopped by a guard, the user or
  root sanity.
