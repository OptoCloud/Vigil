-- A drive's new last-good state.
--   $1 wwn  $2 port  $3 passed  $4 reallocated  $5 pending  $6 power-on hours
--   $7 source path  $8 as of (RFC 3339 text)  $9 serial  $10 reported uncorrect
--   $11 command timeout  $12 UDMA CRC errors  $13 self-test passed  $14 self-test hours
insert into drives (wwn, port, passed, reallocated, pending, power_on_hours, source_path, as_of,
                    serial, reported_uncorrect, command_timeout, udma_crc_errors, self_test_passed, self_test_hours)
values ($1, $2, $3, $4, $5, $6, $7, $8::text::timestamptz, $9, $10, $11, $12, $13, $14)
on conflict (wwn) do update set
    port = excluded.port, passed = excluded.passed, reallocated = excluded.reallocated, pending = excluded.pending,
    power_on_hours = excluded.power_on_hours, source_path = excluded.source_path, as_of = excluded.as_of,
    serial = excluded.serial, reported_uncorrect = excluded.reported_uncorrect,
    command_timeout = excluded.command_timeout, udma_crc_errors = excluded.udma_crc_errors,
    self_test_passed = excluded.self_test_passed, self_test_hours = excluded.self_test_hours
