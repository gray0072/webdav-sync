# 16. Testing and rollout

Tests:

- Unit: decision table (every cell of [§6.2](06-sync-algorithm.md) and every edge case of [§13](13-edge-cases.md) that is pure logic),
  move detection, content hash vectors, name validation, `.cloud.db` importer on a fixture DB.
- Integration: local WebDAV server (in-process test server or `rclone serve webdav` in CI) +
  temp folders; fault injection (drop connections, 429/503/507, slow server, partial listings).
- Soak: random operation generator on both sides, then assert L == R and no data lost.

Rollout on the real data:

1. `wds probe` against the real WebDAV endpoint — record capabilities.
2. Test pair: small separate local folder ⇄ separate remote folder; run scenarios A, B, C.
3. Real pair in `DryRun` with `.cloud.db` import; review the plan.
4. `ApproveAll` with `MaxOpsPerCycle = 20`.
5. `Guarded`, then `Auto`.
