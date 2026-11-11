-- Each pool's last COMPLETED scrub, so its age survives a restart and a resilver that has
-- since replaced it in zpool's report. Epoch milliseconds, formatted by the caller.
select pool, (extract(epoch from max(ended)) * 1000)::int8
from zfs_scans
where function = 'SCRUB' and state = 'FINISHED' and ended is not null
group by pool
order by pool
