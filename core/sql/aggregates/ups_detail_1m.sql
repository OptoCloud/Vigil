-- One row per minute per UPS. The fewest monitors in a bucket is the one that matters: a
-- minute with none logged in was a minute with nothing to shut the host down.
select time_bucket('1 minute', ts) as bucket, ups,
    avg(battery_temperature) as avg_battery_temperature, max(battery_temperature) as max_battery_temperature,
    avg(output_frequency) as avg_output_frequency, min(output_frequency) as min_output_frequency,
    max(output_frequency) as max_output_frequency,
    avg(output_current) as avg_output_current, max(output_current) as max_output_current,
    min(monitors) as min_monitors,
    count(*) as samples
from ups_detail_samples group by 1, 2
