-- Error counters for the vdevs whose counts changed this poll, as parallel arrays: one
-- statement however many moved.
--   $1 poll time (RFC 3339 text)  $2 pools  $3 guids  $4 read  $5 write  $6 checksum  $7 slow
insert into zfs_vdev_errors (ts, pool, guid, read, write, checksum, slow)
select $1::text::timestamptz, pool, guid, read, write, checksum, slow
from unnest($2::text[], $3::text[], $4::int8[], $5::int8[], $6::int8[], $7::int8[])
    as t(pool, guid, read, write, checksum, slow)
