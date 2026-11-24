-- Each pool's fill, percent of its raw size allocated.
--   $1 bucket width (an interval)   $2 oldest timestamp to include
--   $3 newest to include: the end of the window, which is now unless the page looks further back
select pool,
       date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       (100 * sum(allocated_bytes::float8) / nullif(sum(size_bytes::float8), 0))::float8 as fill_percent
from pool_samples
where ts >= $2 and ts <= $3
group by 1, 2
order by 2
