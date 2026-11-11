-- A history row, written only when something other than power-on hours moved: those tick up
-- on every poll and would bury the changes that matter.
--   $1 as of (RFC 3339 text)  $2 wwn  $3 passed  $4 reallocated  $5 pending  $6 power-on hours
--   $7 reported uncorrect  $8 command timeout  $9 UDMA CRC errors  $10 self-test passed
insert into drive_health_history (ts, wwn, passed, reallocated, pending, power_on_hours,
                                  reported_uncorrect, command_timeout, udma_crc_errors, self_test_passed)
values ($1::text::timestamptz, $2, $3, $4, $5, $6, $7, $8, $9, $10)
