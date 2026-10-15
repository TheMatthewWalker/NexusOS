# M2 SQLite migration design

Per-user history will move from JSONL to SQLite using an atomic import marker and retained legacy backup. Migration must not delete existing history, and tests must cover interrupted import and clear/restart semantics. This is a design note only; no implementation is claimed.
