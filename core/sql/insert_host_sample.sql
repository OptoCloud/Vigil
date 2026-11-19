-- One row of the host's load, averaged since the row before. Any value may be null.
--   $1 timestamp (RFC 3339 text)  $2 memory used  $3 cached  $4 arc  $5 swap used (bytes)
--   $6 cpu busy  $7 cpu iowait (%)  $8-$10 load 1, 5, 15  $11-$13 pressure cpu, memory, io (%)
insert into host_samples (ts, memory_used, memory_cached, arc, swap_used, cpu_busy, cpu_iowait,
    load1, load5, load15, pressure_cpu, pressure_memory, pressure_io)
values ($1::text::timestamptz, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
on conflict do nothing
