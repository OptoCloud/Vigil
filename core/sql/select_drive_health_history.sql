-- One drive's recorded health changes, newest first: what the per-disk page charts. A row is
-- only written when something other than power-on hours moved, so this stays short. ts comes
-- back as epoch milliseconds, formatted by the caller, as select_drives does.
--   $1 wwn  $2 how many
select (extract(epoch from ts) * 1000)::int8, passed, reallocated, pending, power_on_hours
from drive_health_history
where wwn = $1
order by ts desc, id desc
limit $2
