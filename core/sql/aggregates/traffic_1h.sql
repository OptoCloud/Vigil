select time_bucket('1 hour', bucket) as bucket, id,
    sum(avg_in_bps * samples) / sum(samples) as avg_in_bps, max(max_in_bps) as max_in_bps,
    sum(avg_out_bps * samples) / sum(samples) as avg_out_bps, max(max_out_bps) as max_out_bps,
    sum(avg_busy_percent * samples) / sum(samples) as avg_busy_percent, max(max_busy_percent) as max_busy_percent,
    sum(samples) as samples
from traffic_1m group by 1, 2
