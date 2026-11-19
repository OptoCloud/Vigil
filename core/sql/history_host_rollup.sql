-- The host's load from a continuous aggregate. {table} is host_1m or host_1h, chosen from a
-- closed set by the range (see HistoryQueries). Weighted by the sample count.
--   $1 bucket width (an interval)   $2 oldest bucket to include
select date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (sum(avg_memory_used * samples) / sum(samples))::float8 as memory_used,
       (sum(avg_memory_cached * samples) / sum(samples))::float8 as memory_cached,
       (sum(avg_arc * samples) / sum(samples))::float8 as arc,
       (sum(avg_swap_used * samples) / sum(samples))::float8 as swap_used,
       (sum(avg_cpu_busy * samples) / sum(samples))::float8 as cpu_busy,
       (sum(avg_cpu_iowait * samples) / sum(samples))::float8 as cpu_iowait,
       (sum(avg_load1 * samples) / sum(samples))::float8 as load1,
       (sum(avg_load5 * samples) / sum(samples))::float8 as load5,
       (sum(avg_load15 * samples) / sum(samples))::float8 as load15,
       (sum(avg_pressure_cpu * samples) / sum(samples))::float8 as pressure_cpu,
       (sum(avg_pressure_memory * samples) / sum(samples))::float8 as pressure_memory,
       (sum(avg_pressure_io * samples) / sum(samples))::float8 as pressure_io,
       max(memory_total)::float8 as memory_total,
       max(swap_total)::float8 as swap_total,
       max(arc_max)::float8 as arc_max,
       (sum(avg_pressure_cpu_full * samples) / sum(samples))::float8 as pressure_cpu_full,
       (sum(avg_pressure_memory_full * samples) / sum(samples))::float8 as pressure_memory_full,
       (sum(avg_pressure_io_full * samples) / sum(samples))::float8 as pressure_io_full
from {table}
where bucket >= $2
group by 1
order by 1
