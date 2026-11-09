# Day 1 — SQLite announcement history

Status: implemented and verified in automated tests, 2026-11-09. No production restart or deployment performed.

## Delivered

- EF Core SQLite 10.0.11, a context factory, initial versioned migration and model snapshot. The old in-memory implementation is removed.
- Startup migration runs before host workers start. Missing directories are created. Migration/path errors stop startup rather than dropping into memory.
- Schedule, starting-soon, individual map/game and series-result markers survive a restart. Composite primary key: destination scope + event key. Event keys contain kind and fixed discipline value as well as provider ID (or local date for schedules).
- A short-lived context per operation avoids sharing EF tracking state across Discord and Quartz callbacks. Idempotent inserts ignore only conflicts on the marker's primary key.
- First successful synchronization defaults to seeding completed historical results, with a persisted per-channel bootstrap marker. Upcoming reminders and the schedule still publish. Optional first-run replay restores the previous behavior.
- Configurable local database path; named Docker volume and a writable directory for the non-root runtime user. Database files, sidecars and local validation outputs are excluded from git/build context.

## Verification

The full suite currently contains 66 passing tests. SQLite tests use real temporary disk files, not EF's in-memory provider. They cover:

- fresh migration, repeated migration, no pending model changes and preservation of data;
- all marker types across disposed/recreated service providers using the same file;
- separation of dates, event kinds, disciplines and announcement channels;
- duplicate/concurrent marker inserts;
- the complete synchronization handler before/after restart, including new results after restart;
- default historical seeding, explicit first-run replay, empty first snapshot and late series completion;
- retry of failed publication after restart without repeating previously recorded messages;
- invalid storage paths fail initialization.

Tests ran under installed SDK 10.0.402 from the ignored work/sdk-validation directory. Build output was isolated under work/sqlite-artifacts because an existing bot process was using normal Debug output. That process was not stopped or restarted. The repository's SDK pin is unchanged.

`docker compose -f compose.development.yaml config --quiet` passed. Docker runtime/image testing was unavailable because the Docker Desktop Linux daemon was not running; writable-directory and named-volume setup were reviewed in source. Production hosting remains Day 2.

## Remaining delivery limits

Discord publication and SQLite commit cannot share a transaction. A crash after a successful send but before saving the marker can replay the message. A failed multi-page schedule can replay already sent pages. This increment does not promise exactly-once delivery or multi-instance publishing coordination.

The previous in-memory bot has no exportable delivery ledger. Initial seeding prevents replaying completed results, but a schedule or upcoming reminder may still post once at the first SQLite deployment. A newly configured channel gets its own independent first-run policy.

Do not delete the database/volume to restart the bot. Offline backups require clean shutdown; live backups require SQLite's backup mechanism. Retention policies and automated backups are future work.
