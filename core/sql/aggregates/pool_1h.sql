select time_bucket('1 hour', bucket) as bucket, pool,
    sum(avg_size_bytes * samples) / sum(samples) as avg_size_bytes,
    sum(avg_allocated_bytes * samples) / sum(samples) as avg_allocated_bytes,
    max(max_allocated_bytes) as max_allocated_bytes, sum(samples) as samples
from pool_1m group by 1, 2
