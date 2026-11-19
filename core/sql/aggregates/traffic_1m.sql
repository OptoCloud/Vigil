-- One row per id per minute. The maxima matter more here than anywhere: a backup saturating a
-- disk for ninety seconds disappears into an hourly average. Kept from the start, because a
-- continuous aggregate's columns cannot be added later.
select time_bucket('1 minute', ts) as bucket, id,
    avg(in_bps) as avg_in_bps, max(in_bps) as max_in_bps,
    avg(out_bps) as avg_out_bps, max(out_bps) as max_out_bps,
    avg(busy_percent) as avg_busy_percent, max(busy_percent) as max_busy_percent,
    count(*) as samples
from traffic_samples group by 1, 2
