-- A scrub or resilver as zpool reports it now. Keyed by when it started, so the same scan
-- is updated in place while it runs and a new one gets its own row.
--   $1 pool  $2 function  $3 started (RFC 3339 text)  $4 ended (RFC 3339 text, null while
--   running)  $5 state  $6 examined bytes  $7 errors
insert into zfs_scans (pool, function, started, ended, state, examined, errors)
values ($1, $2, $3::text::timestamptz, $4::text::timestamptz, $5, $6, $7)
on conflict (pool, function, started) do update set
    ended = excluded.ended, state = excluded.state, examined = excluded.examined,
    errors = excluded.errors, updated = now()
