-- Traffic from a continuous aggregate. {table} is traffic_1m or traffic_1h (see HistoryQueries).
--   $1 bucket width (an interval)   $2 oldest bucket to include
--   $3 newest to include: the end of the window, which is now unless the page looks further back
select id,
       date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (sum(avg_in_bps * samples) / sum(samples))::float8 as in_bps,
       (sum(avg_out_bps * samples) / sum(samples))::float8 as out_bps,
       (sum(avg_busy_percent * samples) / sum(samples))::float8 as busy_percent
from {table}
where bucket >= $2 and bucket <= $3
group by 1, 2
order by 2
