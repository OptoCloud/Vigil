-- Every pool's size and allocation from one ZFS poll, as parallel arrays.
--   $1 timestamp (RFC 3339 text)  $2 pools  $3 size bytes  $4 allocated bytes
insert into pool_samples (ts, pool, size_bytes, allocated_bytes)
select $1::text::timestamptz, pool, size_bytes, allocated_bytes
from unnest($2::text[], $3::int8[], $4::int8[]) as t(pool, size_bytes, allocated_bytes)
on conflict do nothing
