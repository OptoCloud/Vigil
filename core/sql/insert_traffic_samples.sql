-- Every disk's, pool's and interface's traffic at one moment, as parallel arrays.
--   $1 timestamp (RFC 3339 text)  $2 ids  $3 in B/s  $4 out B/s  $5 busy % (null but for disks)
insert into traffic_samples (ts, id, in_bps, out_bps, busy_percent)
select $1::text::timestamptz, id, in_bps, out_bps, busy_percent
from unnest($2::text[], $3::real[], $4::real[], $5::real[]) as t(id, in_bps, out_bps, busy_percent)
on conflict do nothing
