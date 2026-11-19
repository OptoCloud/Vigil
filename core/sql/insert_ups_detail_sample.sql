-- The rest of one UPS poll (ADR-026), written beside insert_ups_sample. Any value may be null.
--   $1 timestamp (RFC 3339 text)  $2 ups  $3 battery °C  $4 output Hz  $5 output A
--   $6 monitors logged in
insert into ups_detail_samples (ts, ups, battery_temperature, output_frequency, output_current, monitors)
values ($1::text::timestamptz, $2, $3, $4, $5, $6)
on conflict do nothing
