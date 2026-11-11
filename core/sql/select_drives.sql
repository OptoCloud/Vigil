-- Every drive's last good health result. as_of comes back as epoch milliseconds, which
-- vigil-protocol formats, so no date/time handling crosses the boundary as text.
select wwn, port, passed, reallocated, pending, power_on_hours, source_path,
       (extract(epoch from as_of) * 1000)::int8,
       serial, reported_uncorrect, command_timeout, udma_crc_errors, self_test_passed, self_test_hours
from drives
order by wwn
