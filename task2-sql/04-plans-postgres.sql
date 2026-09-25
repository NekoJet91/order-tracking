-- Reproduces the measurements quoted in README.md.
--
-- Loads five million synthetic payments, then runs three implementations of the same
-- requirement side by side. The point is not that one is faster; it is *why*, and the plans
-- printed below say it more convincingly than prose can.
--
-- Takes about a minute, most of it generating rows.
--
--   psql -v ON_ERROR_STOP=1 -f 04-plans-postgres.sql
--
-- Destructive: it truncates client."Payments". Run it on a scratch database.

\set ON_ERROR_STOP on
\timing off

-- ---------------------------------------------------------------------------- bulk data --

TRUNCATE client."Payments";

-- Five million payments, two thousand clients, eleven years. That puts roughly 1 200
-- payments on the client the queries below ask about, which is the realistic shape: the
-- table is large, one client's slice of it is not.
INSERT INTO client."Payments" ("ClientId", "Dt", "Amount")
SELECT (random() * 1999)::bigint + 1,
       '2015-01-01'::timestamp + (random() * 4017)::int * interval '1 day'
                               + (random() * 86399)::int * interval '1 second',
       (random() * 10000)::numeric(19,4)
FROM generate_series(1, 5000000);

-- ANALYZE for the statistics, VACUUM for the visibility map. The second one matters more
-- than it looks: without it there can be no index-only scan, and the last section below
-- shows what that does to one of the three candidates.
VACUUM (ANALYZE) client."Payments";

-- ------------------------------------------------------------------------- the candidates --

-- A: the range restriction lives in the join condition, evaluated per day.
CREATE FUNCTION pg_temp.totals_a(p_client_id bigint, p_sd date, p_ed date)
RETURNS TABLE ("Dt" date, "Сумма" numeric(19,4)) LANGUAGE sql STABLE AS $$
    SELECT day::date, COALESCE(SUM(p."Amount"), 0)::numeric(19,4)
    FROM generate_series(p_sd, p_ed, interval '1 day') AS day
    LEFT JOIN client."Payments" AS p
           ON p."ClientId" = p_client_id
          AND p."Dt" >= day AND p."Dt" < day + interval '1 day'
    GROUP BY day ORDER BY day;
$$;

-- B: the join matches on the date part of the timestamp.
CREATE FUNCTION pg_temp.totals_b(p_client_id bigint, p_sd date, p_ed date)
RETURNS TABLE ("Dt" date, "Сумма" numeric(19,4)) LANGUAGE sql STABLE AS $$
    SELECT day::date, COALESCE(SUM(p."Amount"), 0)::numeric(19,4)
    FROM generate_series(p_sd, p_ed, interval '1 day') AS day
    LEFT JOIN client."Payments" AS p
           ON p."ClientId" = p_client_id
          AND p."Dt"::date = day::date
    GROUP BY day ORDER BY day;
$$;

-- C is client.get_daily_payment_totals, the shipped function.

-- ------------------------------------------------------------------------------- wide span --

\echo ''
\echo '################################################################'
\echo '# WIDE: eleven years, 3654 days'
\echo '################################################################'

\echo ''
\echo '--- A: range in the join ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM pg_temp.totals_a(1, '2015-01-01', '2025-01-01');

\echo ''
\echo '--- B: cast in the join ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM pg_temp.totals_b(1, '2015-01-01', '2025-01-01');

\echo ''
\echo '--- C: pre-aggregate, equality join (shipped) ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM client.get_daily_payment_totals(1, '2015-01-01', '2025-01-01');

-- ----------------------------------------------------------------------------- narrow span --

\echo ''
\echo '################################################################'
\echo '# NARROW: 7 days out of the same eleven years'
\echo '################################################################'

\echo ''
\echo '--- A: range in the join ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM pg_temp.totals_a(1, '2022-06-01', '2022-06-07');

\echo ''
\echo '--- B: cast in the join ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM pg_temp.totals_b(1, '2022-06-01', '2022-06-07');

\echo ''
\echo '--- C: pre-aggregate, equality join (shipped) ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM client.get_daily_payment_totals(1, '2022-06-01', '2022-06-07');

-- ------------------------------------------------------- wide span, no index-only scans --

-- Simulates a table that has just been bulk-loaded and whose visibility map autovacuum has
-- not caught up with yet — a perfectly ordinary state for a payments table under load.
\echo ''
\echo '################################################################'
\echo '# WIDE, with index-only scans unavailable'
\echo '################################################################'

SET enable_indexonlyscan = off;

\echo ''
\echo '--- A: range in the join ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM pg_temp.totals_a(1, '2015-01-01', '2025-01-01');

\echo ''
\echo '--- B: cast in the join ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM pg_temp.totals_b(1, '2015-01-01', '2025-01-01');

\echo ''
\echo '--- C: pre-aggregate, equality join (shipped) ---'
EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
SELECT * FROM client.get_daily_payment_totals(1, '2015-01-01', '2025-01-01');

RESET enable_indexonlyscan;
