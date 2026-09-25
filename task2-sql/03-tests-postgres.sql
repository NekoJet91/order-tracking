-- Self-checking tests. Prints one line per case and raises on the first mismatch, so a clean
-- run means every assertion held rather than meaning someone read the output carefully.
--
-- Everything happens in one transaction that rolls back, including the fixture rows and the
-- assertion helper, so the script can be run repeatedly and leaves no state behind.
--
--   psql -v ON_ERROR_STOP=1 -f 03-tests-postgres.sql

\set ON_ERROR_STOP on

BEGIN;

-- Everything below runs against a known table rather than whatever happens to be there, so
-- the assertions hold no matter what state the database was left in — in particular after
-- 04-plans-postgres.sql has loaded its five million rows. The transaction rolls back, so this
-- deletes nothing permanently.
DELETE FROM client."Payments";

-- The six rows from the brief. Id is left to the sequence; the function never reads it.
INSERT INTO client."Payments" ("ClientId", "Dt", "Amount") VALUES
    (1, '2022-01-03 17:24:00', 100),
    (1, '2022-01-05 17:24:14', 200),
    (1, '2022-01-05 18:23:34', 250),
    (1, '2022-01-07 10:12:38',  50),
    (2, '2022-01-05 17:24:14', 278),
    (2, '2022-01-10 12:39:29', 300);

-- Client 3 exists to put payments exactly on the day boundaries, which is where a predicate
-- written as a closed range or a cast to date goes wrong.
INSERT INTO client."Payments" ("ClientId", "Dt", "Amount") VALUES
    (3, '2022-03-01 00:00:00', 10),
    (3, '2022-03-01 23:59:59', 20),
    (3, '2022-03-02 00:00:00', 40);

-- Client 4 pays and is then refunded on the same day.
INSERT INTO client."Payments" ("ClientId", "Dt", "Amount") VALUES
    (4, '2022-04-01 09:00:00',  500),
    (4, '2022-04-01 17:00:00', -120);

-- ---------------------------------------------------------------------------- assertions --

CREATE FUNCTION pg_temp.assert_totals(
    p_case      text,
    p_client_id bigint,
    p_sd        date,
    p_ed        date,
    p_days      date[],
    p_amounts   numeric[]
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_actual_count integer;
    v_wrong        integer;
    v_detail       text;
BEGIN
    -- WITH ORDINALITY compares by position, so this asserts the order of the rows as well as
    -- their contents. Ordering is part of the contract: the result is a calendar.
    SELECT count(*),
           count(*) FILTER (WHERE t."Dt" IS DISTINCT FROM p_days[t.ord]
                               OR t."Сумма" IS DISTINCT FROM p_amounts[t.ord]),
           string_agg(
               format('    row %s: got %s = %s, expected %s = %s',
                      t.ord, t."Dt", t."Сумма", p_days[t.ord], p_amounts[t.ord]),
               E'\n')
               FILTER (WHERE t."Dt" IS DISTINCT FROM p_days[t.ord]
                          OR t."Сумма" IS DISTINCT FROM p_amounts[t.ord])
      INTO v_actual_count, v_wrong, v_detail
      FROM client.get_daily_payment_totals(p_client_id, p_sd, p_ed)
           WITH ORDINALITY AS t("Dt", "Сумма", ord);

    IF v_actual_count <> coalesce(array_length(p_days, 1), 0) THEN
        RAISE EXCEPTION 'FAIL  %: % row(s), expected %',
            p_case, v_actual_count, coalesce(array_length(p_days, 1), 0);
    END IF;

    IF v_wrong > 0 THEN
        RAISE EXCEPTION E'FAIL  %: % wrong row(s)\n%', p_case, v_wrong, v_detail;
    END IF;

    RAISE NOTICE 'ok    % (% rows)', p_case, v_actual_count;
END;
$$;

-- --------------------------------------------------------------- the brief's own examples --

SELECT pg_temp.assert_totals(
    'worked example 1 — client 1, 02..07 Jan',
    1, '2022-01-02', '2022-01-07',
    ARRAY['2022-01-02','2022-01-03','2022-01-04','2022-01-05','2022-01-06','2022-01-07']::date[],
    ARRAY[0, 100, 0, 450, 0, 50]::numeric[]);

SELECT pg_temp.assert_totals(
    'worked example 2 — client 2, 04..11 Jan',
    2, '2022-01-04', '2022-01-11',
    ARRAY['2022-01-04','2022-01-05','2022-01-06','2022-01-07',
          '2022-01-08','2022-01-09','2022-01-10','2022-01-11']::date[],
    ARRAY[0, 278, 0, 0, 0, 0, 300, 0]::numeric[]);

-- --------------------------------------------------------------------------- day boundaries --

-- Midnight belongs to the day that starts, not the one that ends; 23:59:59 belongs to the day
-- it is in. Together these two rows are what a closed range `BETWEEN d AND d + 1 day` would
-- get wrong, by counting 2022-03-02 00:00:00 in both days.
SELECT pg_temp.assert_totals(
    'a payment at 00:00:00 and one at 23:59:59 land on the same day',
    3, '2022-03-01', '2022-03-02',
    ARRAY['2022-03-01','2022-03-02']::date[],
    ARRAY[30, 40]::numeric[]);

-- --------------------------------------------------------------------------------- amounts --

SELECT pg_temp.assert_totals(
    'a refund nets off within its day',
    4, '2022-04-01', '2022-04-01',
    ARRAY['2022-04-01']::date[],
    ARRAY[380]::numeric[]);

-- ------------------------------------------------------------------------------- intervals --

SELECT pg_temp.assert_totals(
    'a single-day interval returns exactly one row',
    1, '2022-01-05', '2022-01-05',
    ARRAY['2022-01-05']::date[],
    ARRAY[450]::numeric[]);

SELECT pg_temp.assert_totals(
    'a reversed interval returns nothing',
    1, '2022-01-07', '2022-01-02',
    ARRAY[]::date[], ARRAY[]::numeric[]);

SELECT pg_temp.assert_totals(
    'a null bound returns nothing',
    1, NULL, '2022-01-07',
    ARRAY[]::date[], ARRAY[]::numeric[]);

-- ------------------------------------------------------------------------------- no payments --

-- A client that exists in the table but paid nothing in the window, and a client id that has
-- never been seen, are the same case to this function, and deliberately so: the calendar comes
-- from generate_series, not from the data.
SELECT pg_temp.assert_totals(
    'a client with no payments in the window gets zeros, not an empty result',
    1, '2021-06-01', '2021-06-03',
    ARRAY['2021-06-01','2021-06-02','2021-06-03']::date[],
    ARRAY[0, 0, 0]::numeric[]);

SELECT pg_temp.assert_totals(
    'an unknown client gets the same',
    999999, '2022-01-01', '2022-01-02',
    ARRAY['2022-01-01','2022-01-02']::date[],
    ARRAY[0, 0]::numeric[]);

-- ------------------------------------------------------------------------- multi-year span --

-- The brief says intervals may span several years, so this is the case it is pointing at.
-- Asserted by shape rather than by a literal array of 3 653 dates.
DO $$
DECLARE
    v_rows       integer;
    v_first      date;
    v_last       date;
    v_total      numeric;
    v_nulls      integer;
    v_out_of_seq integer;
BEGIN
    SELECT count(*), min("Dt"), max("Dt"), sum("Сумма"),
           count(*) FILTER (WHERE "Сумма" IS NULL),
           count(*) FILTER (WHERE "Dt" <> '2015-01-01'::date + (ord - 1)::int)
      INTO v_rows, v_first, v_last, v_total, v_nulls, v_out_of_seq
      FROM client.get_daily_payment_totals(1, '2015-01-01', '2024-12-31')
           WITH ORDINALITY AS t("Dt", "Сумма", ord);

    IF v_rows <> 3653 THEN
        RAISE EXCEPTION 'FAIL  ten-year interval: % rows, expected 3653', v_rows;
    END IF;
    IF v_first <> '2015-01-01' OR v_last <> '2024-12-31' THEN
        RAISE EXCEPTION 'FAIL  ten-year interval: bounds % .. %', v_first, v_last;
    END IF;
    IF v_nulls > 0 THEN
        RAISE EXCEPTION 'FAIL  ten-year interval: % null total(s); empty days must be 0', v_nulls;
    END IF;
    IF v_out_of_seq > 0 THEN
        RAISE EXCEPTION 'FAIL  ten-year interval: % day(s) out of sequence', v_out_of_seq;
    END IF;
    -- Every payment client 1 ever made falls inside this window, so the totals must add up to
    -- the whole of it. This is what catches a window that silently drops its last day.
    IF v_total <> 600 THEN
        RAISE EXCEPTION 'FAIL  ten-year interval: total %, expected 600', v_total;
    END IF;

    RAISE NOTICE 'ok    a ten-year interval yields 3653 consecutive days, no nulls, total 600';
END;
$$;

ROLLBACK;

\echo ''
\echo 'All assertions passed. The transaction was rolled back; the table is as it was.'
