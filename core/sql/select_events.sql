-- A page of the event log, newest first. The first page is a page's first paint (the live stream
-- only carries events raised after it connects); the next is asked for with the oldest id of
-- the one before. By id, which is the order events were recorded in and is the primary key, so
-- a page deep in the log costs what the first does. ts comes back as epoch milliseconds,
-- formatted by the caller, as select_drives does.
--   $1 how many  $2 only events with an id below this, or null for the newest
--   $3 the severities wanted
select id, (extract(epoch from ts) * 1000)::int8, severity, kind, message
from events
where ($2::int8 is null or id < $2)
  and severity = any($3)
order by id desc
limit $1
