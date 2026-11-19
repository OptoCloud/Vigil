select time_bucket('1 minute', ts) as bucket, pool,
    avg(size_bytes) as avg_size_bytes, avg(allocated_bytes) as avg_allocated_bytes,
    max(allocated_bytes) as max_allocated_bytes, count(*) as samples
from pool_samples group by 1, 2
