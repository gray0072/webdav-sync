# 7. Scenarios

## 7.1 A — Cloud → empty local folder

Remote has data, local root is empty, no base. All remote items → `Download`. No deletes possible.

## 7.2 B — Cloud → existing (non-empty) local folder

No base. **Union merge**, never delete:

- Present on both sides, same size & hash → UpdateBase.
- Present on both, different → Conflict (keep both).
- Present on one side only → copy to the other side.

The plan is always shown for approval in this scenario (regardless of mode), with a summary:
"N to download, M to upload, K identical, C conflicts".

## 7.3 C — Local folder → empty cloud

Remote root empty, no base. All local items → `Upload`/`MkdirRemote`. Quota checked before start.

## 7.4 D — Resume from the legacy client (`.cloud.db` present)

Import baseline ([§3.4](03-legacy-state.md)), then run a normal cycle. Expected effect: only changes made since
the old client stopped are propagated. Because local deletions since then are real, they
will appear as `DeleteRemote` — mass-delete guard and approval apply. The imported baseline counts as a
completed initial sync (no union mode); unchanged items only get `UpdateBase` (the remote identity is adopted,
[§3.5](03-legacy-state.md)), nothing is transferred for them. The first cycle creates the remote marker.

## 7.5 E — Normal operation

Base exists; incremental cycles as in [§6](06-sync-algorithm.md).

## 7.6 Initial sync rule

While a pair has never completed a full cycle ("initial sync"), the engine runs in
**union mode**: deletes are never planned; disagreements become conflicts. Deletions are
enabled only after the first full successful cycle: every planned op was selected and completed, and both
snapshots were complete ([§6.8](06-sync-algorithm.md)). A plan with nothing to do on complete snapshots also
completes the initial sync. Tests: `Scenario_A…`, `Scenario_B…`, `Scenario_C…`, `Scenario_D…`, `Scenario_E…`
(unit, in-memory remote) and the integration tests against `rclone serve webdav`.
