# Persistence Demo

A walkthrough for running the M4 persistence layer end-to-end via `Sim.Host`.

The deterministic guarantee — kill the process mid-flight, restart, and the
resumed world reaches the identical hash an uninterrupted run would — is
exhaustively tested in `tests/Sim.Persistence.Tests/RecoveryTests.cs` (see
`CrashRecoveryMatchesUninterrupted` and
`PreSnapshotIntentsCanBeDeleted_StillRecovers`). This doc is the human-runnable
confirmation: a small scripted scenario you can start, kill, restart, and
observe.

## Cold start

```bash
rm -rf /tmp/aow-demo
dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo
```

Output:

```
Genesis seeded at /tmp/aow-demo; initial snapshot at tick 0.
Target tick reached; final snapshot at tick 360.
Final hash: 2908E3D6...
```

What just happened:

- `/tmp/aow-demo/sim.db` (intent log) and `/tmp/aow-demo/snapshots.db`
  (snapshot store) were created.
- A small scenario was seeded: a 20×20 grid, a builder unit, a hauler unit,
  a stockpile pre-loaded with wood. Two long-running intents were submitted
  via `DurableSubmit.SubmitIntentDurable` (log-then-apply):
  - Move unit 1 from (0,0) to (18,18).
  - Haul wood from the stockpile to the castle (round trip).
- The host ran the sim forward in 100-tick batches, snapshotting on the
  default cadence (5000 ticks OR 100 intents — neither hit in this short
  scenario, so the only snapshots are the initial one and the final one).
- After the scenario completed (queue drained), a final snapshot was saved.

## Recovery (clean restart)

```bash
dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo
```

Output:

```
Recovered at tick 360; resumed with 0 in-flight events.
Target tick reached; final snapshot at tick 360.
Final hash: 2908E3D6...
```

The final hash matches the first run exactly. `Recover` loaded the snapshot
saved at tick 360, found no intents after that tick, and produced a
ready-to-run sim that's identical to the one that exited.

## Recovery (kill mid-flight)

The scenario completes quickly on modern hardware (tens of milliseconds), so
catching it mid-flight by hand is tricky. To make the demo richer, edit
`PersistentDemo.ColdStart` in `src/Sim.Host/Program.cs` to submit a longer
script of intents, or lower `DefaultTargetTick` to stop the host before the
queue drains. Either way, the contract is the same:

```bash
rm -rf /tmp/aow-demo
dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo &
HOST=$!
sleep 0.1                      # let it run a bit
kill -9 $HOST                  # mid-flight SIGKILL — no clean shutdown
wait $HOST 2>/dev/null

dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo
# "Recovered at tick T; resumed with N in-flight events."
# scenario continues; final hash matches an uninterrupted run on the same
# intent sequence.
```

The xUnit gate (`RecoveryTests.CrashRecoveryMatchesUninterrupted`) covers
this case exhaustively — multiple kinds of in-flight processes (mid-walk,
mid-haul, mid-production, mid-group-formation) all resume to the identical
post-recovery hash.

## What's in the data dir

- `sim.db` — intent log. SQLite, WAL mode. One row per intent, ordered by
  `(tick, seq)`. JSON payload column is human-readable for auditing:
  ```bash
  sqlite3 /tmp/aow-demo/sim.db "SELECT tick, seq, type, payload FROM intents;"
  ```
- `snapshots.db` — snapshot store. SQLite, WAL mode. One row per snapshot
  tick, with a versioned binary blob. The retention policy keeps the last 3.
  ```bash
  sqlite3 /tmp/aow-demo/snapshots.db "SELECT tick, version, length(blob), created_at FROM snapshots;"
  ```

Both stores are POSIX-friendly SQLite files; copy the data dir, restart with
`--data-dir <copy>`, and the world resumes from the copied state. Useful for
debug snapshots, branch testing, and audit.

## Snapshot-on-deploy

When the binary changes incompatibly (a snapshot format version bump, a new
intent type, etc.), the operator playbook is:

1. **Quiesce** the live process (stop accepting new intents).
2. **Snapshot** under the OLD binary (drains in-flight events into anchors).
3. **Deploy** the NEW binary.
4. **Restart** with `--data-dir`; Recovery reads the snapshot.
   - If the format version is unchanged: clean resume.
   - If bumped without migration support: `Snapshot.Restore` throws
     `InvalidDataException` pointing at this playbook. Operator runs an
     interim migration step (out of scope for M4).

The host doesn't automate this — it's policy, not code. See
`docs/persistence-model.md` "Operator playbook" for the full discussion.
