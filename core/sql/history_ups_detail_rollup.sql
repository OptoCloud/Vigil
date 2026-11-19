-- {table} is ups_detail_1m or ups_detail_1h. Weighted by the sample count; monitors is the fewest.
--   $1 bucket width (an interval)   $2 oldest bucket to include   $3 the UPS's name
select date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (sum(avg_battery_temperature * samples) / sum(samples))::float8 as battery_temperature,
       (sum(avg_output_frequency * samples) / sum(samples))::float8 as output_frequency,
       (sum(avg_output_current * samples) / sum(samples))::float8 as output_current,
       min(min_monitors)::float8 as monitors
from {table}
where ups = $3 and bucket >= $2
group by 1
order by 1
