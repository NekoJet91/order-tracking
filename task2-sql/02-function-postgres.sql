-- The deliverable: daily payment totals for one client over a date interval, with 0 for the
-- days on which nothing was paid. Both endpoints inclusive.
--
-- Three things decide the shape of this query, and the README works through the measurements
-- behind each:
--
--   1. The calendar comes from generate_series, not from the data. A day with no payments
--      cannot be produced by grouping a table that has no row for it.
--   2. The date range is a half-open predicate in the WHERE clause, over the whole interval
--      at once. That is a single index range scan; the same restriction written per-day in
--      the join condition becomes one index probe per day.
--   3. The payments are aggregated first and the calendar is joined to the result on
--      equality. An equality join can be merged or hashed; a range join can only be a nested
--      loop.

CREATE OR REPLACE FUNCTION client.get_daily_payment_totals(
    p_client_id bigint,
    p_sd        date,
    p_ed        date
)
RETURNS TABLE ("Dt" date, "Сумма" numeric(19,4))
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    SELECT calendar.day::date                        AS "Dt",
           COALESCE(paid.total, 0)::numeric(19,4)    AS "Сумма"
    FROM generate_series(p_sd, p_ed, interval '1 day') AS calendar(day)
    LEFT JOIN (
        SELECT p."Dt"::date       AS paid_on,
               SUM(p."Amount")    AS total
        FROM client."Payments" AS p
        WHERE p."ClientId" = p_client_id
          -- Half-open, and on the raw column. Dt carries a time, so the upper bound has to be
          -- the midnight that starts the day after p_ed rather than p_ed itself, or every
          -- payment made during the last day of the interval is lost.
          AND p."Dt" >= p_sd
          AND p."Dt" <  p_ed + 1
        -- The cast is harmless here: it groups rows the index has already selected, rather
        -- than deciding which rows to read.
        GROUP BY 1
    ) AS paid ON paid.paid_on = calendar.day::date
    ORDER BY calendar.day;
$$;

COMMENT ON FUNCTION client.get_daily_payment_totals(bigint, date, date) IS
'Per-day payment totals for one client between two inclusive dates. Days with no payments '
'return 0. Returns no rows if either bound is null or the interval is reversed.';
