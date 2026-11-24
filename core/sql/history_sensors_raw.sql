-- Temperatures from the raw samples, bucketed down to a few hundred points per series so a
-- one-hour chart costs the browser no more than a one-year one.
--   $1 bucket width (an interval, e.g. '10 seconds')   $2 oldest timestamp to include
--   $3 newest to include: the end of the window, which is now unless the page looks further back
--
-- date_bin is anchored at the epoch rather than at $2, so the buckets of two requests a second
-- apart line up and the chart does not shimmer as it refreshes.
select sensor_id as id,
       date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       avg(celsius)::float8 as value
from sensor_samples
where ts >= $2 and ts <= $3
group by 1, 2
order by 2
