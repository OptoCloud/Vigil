select time_bucket('1 hour', bucket) as bucket, ups,
    sum(avg_battery_temperature * samples) / sum(samples) as avg_battery_temperature,
    max(max_battery_temperature) as max_battery_temperature,
    sum(avg_output_frequency * samples) / sum(samples) as avg_output_frequency,
    min(min_output_frequency) as min_output_frequency, max(max_output_frequency) as max_output_frequency,
    sum(avg_output_current * samples) / sum(samples) as avg_output_current,
    max(max_output_current) as max_output_current,
    min(min_monitors) as min_monitors,
    sum(samples) as samples
from ups_detail_1m group by 1, 2
