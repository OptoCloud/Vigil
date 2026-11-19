-- Each pool's fill from a continuous aggregate. {table} is pool_1m or pool_1h (see HistoryQueries).
--   $1 bucket width (an interval)   $2 oldest bucket to include
select pool,
       date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (100 * sum(avg_allocated_bytes * samples) / nullif(sum(avg_size_bytes * samples), 0))::float8 as fill_percent
from {table}
where bucket >= $2
group by 1, 2
order by 2
