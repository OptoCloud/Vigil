-- The UPS's later readings (ADR-026) from the raw samples.
--   $1 bucket width (an interval)   $2 oldest timestamp to include   $3 the UPS's name
select date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       avg(battery_temperature)::float8 as battery_temperature,
       avg(output_frequency)::float8 as output_frequency,
       avg(output_current)::float8 as output_current,
       min(monitors)::float8 as monitors
from ups_detail_samples
where ups = $3 and ts >= $2
group by 1
order by 1
