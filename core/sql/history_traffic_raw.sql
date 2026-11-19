-- Disk, pool and interface traffic from the raw samples.
--   $1 bucket width (an interval)   $2 oldest timestamp to include
select id,
       date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       avg(in_bps)::float8 as in_bps,
       avg(out_bps)::float8 as out_bps,
       avg(busy_percent)::float8 as busy_percent
from traffic_samples
where ts >= $2
group by 1, 2
order by 2
