-- One row per minute over the raw host samples. The maxima are kept although no chart reads
-- them yet: a continuous aggregate's columns cannot be added later without rebuilding it from
-- raw rows that retention has already dropped (ADR-026). The uptime's minimum and maximum
-- straddle a reboot inside the bucket.
select time_bucket('1 minute', ts) as bucket,
    avg(memory_used) as avg_memory_used, max(memory_used) as max_memory_used,
    avg(memory_cached) as avg_memory_cached, avg(arc) as avg_arc,
    avg(swap_used) as avg_swap_used, max(swap_used) as max_swap_used,
    avg(cpu_busy) as avg_cpu_busy, max(cpu_busy) as max_cpu_busy, avg(cpu_iowait) as avg_cpu_iowait,
    avg(load1) as avg_load1, max(load1) as max_load1, avg(load5) as avg_load5, avg(load15) as avg_load15,
    avg(pressure_cpu) as avg_pressure_cpu, avg(pressure_memory) as avg_pressure_memory,
    avg(pressure_io) as avg_pressure_io, max(pressure_io) as max_pressure_io,
    max(memory_total) as memory_total, max(swap_total) as swap_total, max(arc_max) as arc_max,
    avg(pressure_cpu_full) as avg_pressure_cpu_full,
    avg(pressure_memory_full) as avg_pressure_memory_full, max(pressure_memory_full) as max_pressure_memory_full,
    avg(pressure_io_full) as avg_pressure_io_full, max(pressure_io_full) as max_pressure_io_full,
    min(uptime_seconds) as min_uptime_seconds, max(uptime_seconds) as max_uptime_seconds,
    count(*) as samples
from host_samples group by 1
