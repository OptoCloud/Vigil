-- The host's load from the raw samples.
--   $1 bucket width (an interval)   $2 oldest timestamp to include
--   $3 newest to include: the end of the window, which is now unless the page looks further back
select date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       avg(memory_used)::float8 as memory_used,
       avg(memory_cached)::float8 as memory_cached,
       avg(arc)::float8 as arc,
       avg(swap_used)::float8 as swap_used,
       avg(cpu_busy)::float8 as cpu_busy,
       avg(cpu_iowait)::float8 as cpu_iowait,
       avg(load1)::float8 as load1,
       avg(load5)::float8 as load5,
       avg(load15)::float8 as load15,
       avg(pressure_cpu)::float8 as pressure_cpu,
       avg(pressure_memory)::float8 as pressure_memory,
       avg(pressure_io)::float8 as pressure_io,
       max(memory_total)::float8 as memory_total,
       max(swap_total)::float8 as swap_total,
       max(arc_max)::float8 as arc_max,
       avg(pressure_cpu_full)::float8 as pressure_cpu_full,
       avg(pressure_memory_full)::float8 as pressure_memory_full,
       avg(pressure_io_full)::float8 as pressure_io_full
from host_samples
where ts >= $2 and ts <= $3
group by 1
order by 1
