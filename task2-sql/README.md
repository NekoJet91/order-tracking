# Task 2 — daily payment totals

A table-valued function taking a client and a date interval, returning one row per calendar
day with that client's payments for the day and `0` on the days nothing was paid. Both
endpoints inclusive; intervals may span years.

```
ClientPayments (Id bigint, ClientId bigint, Dt datetime2(0), Amount money)
```

## The files

| File | What it is |
|---|---|
| `01-schema-postgres.sql` | Table and index |
| `02-function-postgres.sql` | **The answer.** Developed and measured against PostgreSQL 14 |
| `03-tests-postgres.sql` | Ten assertions, including the brief's two worked examples. Raises on the first failure |
| `04-plans-postgres.sql` | Loads five million rows and prints the plans quoted below |
| `05-function-mssql.sql` | SQL Server translation. **Not run** — no instance was available |

```bash
createdb payments_task
psql -d payments_task -v ON_ERROR_STOP=1 -f 01-schema-postgres.sql
psql -d payments_task -v ON_ERROR_STOP=1 -f 02-function-postgres.sql
psql -d payments_task -v ON_ERROR_STOP=1 -f 03-tests-postgres.sql   # ten ok lines
psql -d payments_task -v ON_ERROR_STOP=1 -f 04-plans-postgres.sql   # about a minute
```

The tests run inside a transaction that rolls back, so they are re-runnable, leave the table
as they found it, and hold regardless of what is in it — including the five million rows
`04-plans-postgres.sql` loads.

## The shape of the answer

**The calendar comes from `generate_series`, not from the data.** A day with no payments has
no row to group, so the days are generated and the payments outer-joined onto them. A
`GROUP BY` over `Payments` alone cannot satisfy the specification however it is written.

**`LEFT JOIN` keeps the empty days, `COALESCE` gives them their value.** The outer join keeps
the row; the coalesce turns `SUM` over zero rows from `NULL` into `0`.

**The date range is half-open, on the raw column, in the `WHERE` clause.**

```sql
WHERE p."ClientId" = p_client_id
  AND p."Dt" >= p_sd
  AND p."Dt" <  p_ed + 1
```

`Dt` carries a time, so the upper bound is the midnight that *starts* the day after `p_ed`.
`<= p_ed` drops every payment made during the last day of the interval; `BETWEEN p_sd AND
p_ed + 1` counts the following midnight twice. Two assertions pin this down: payments at
`00:00:00` and `23:59:59` land on the same day, and the next midnight does not.

## Why this shape and not the two obvious ones

Two other forms are also correct, and were measured against this one:

- **A** — the range written per-day in the join condition.
- **B** — the join matching on `p."Dt"::date = day`.
- **C** — the shipped function: restrict once in `WHERE`, aggregate by day, join the calendar
  to the result on equality.

Five million payments, two thousand clients, eleven years, so about 1 200 payments belong to
the client being queried. One run of `04-plans-postgres.sql`; the rows are generated randomly,
so the numbers move between runs but the shape does not.

| | Wide (11 years, 3 654 days) | Narrow (7 days) | Wide, no index-only scan |
|---|---|---|---|
| **A** — range in the join | 6.1 ms, 11 199 buffers | 0.05 ms, 22 buffers | **611 ms**, 1 251 buffers |
| **B** — `::date` in the join | 3.8 ms, 260 buffers | 0.29 ms, 260 buffers | 6.2 ms, 1 251 buffers |
| **C** — pre-aggregate, equality join | **2.2 ms, 240 buffers** | 0.07 ms, **4 buffers** | **3.3 ms**, 1 153 buffers |

The buffer counts carry more than the timings, which at the narrow window are noise. They say
what each query has to touch:

- **A grows with the number of days.** The range sits in the join, so it is evaluated per day:
  3 654 separate index probes, linear in something unrelated to how much was paid.
- **B grows with the client's whole history and ignores the window.** 260 buffers whether the
  interval is seven days or eleven years, because the cast hides `Dt` from the index and
  `ClientId` is the only predicate left to seek on. A client of ten years' standing asking
  about last week has their entire history read to answer a seven-row question.
- **C touches what the answer needs.** 4 buffers narrow, 240 wide.

**The 611 ms is variant A with index-only scans unavailable** — a freshly bulk-loaded table
whose visibility map autovacuum has not reached yet, which is an ordinary state for a payments
table rather than a contrivance. The planner falls back to materialising the client's payments
and re-filtering them once per day: 4 694 215 rows removed by the join filter, a hundredfold
collapse against A's own healthy 6.1 ms, caused by nothing in the query or the data. C is a
hash join either way.

So the shipped function does both things at once. The range is applied once, over the whole
interval, where the index can serve it; and the calendar joins to the pre-aggregated result on
an **equality**, which can be merged or hashed. A range join can only be a nested loop.

```
 Hash Left Join (rows=3654)
   Hash Cond: ((calendar.day)::date = paid.paid_on)
   Buffers: shared hit=237
   ->  Function Scan on generate_series calendar (rows=3654)
   ->  HashAggregate (rows=989)
         Group Key: (p."Dt")::date
         ->  Index Only Scan using "IX_Payments_ClientId_Dt" (rows=1164)
               Index Cond: (("ClientId" = 1) AND ("Dt" >= '2015-01-01') AND ("Dt" < '2025-01-02'))
               Heap Fetches: 0
 Execution Time: 2.190 ms
```

`Heap Fetches: 0` is the `INCLUDE ("Amount")` earning its place: the totals are computed
without the table being touched.

C casts `Dt` to `date` as well, in the `GROUP BY`. A cast is harmful where it decides which
rows to read, and harmless where it groups rows the index has already selected.

A correlated scalar subquery per day is a fourth form, reading more directly and needing no
`GROUP BY`, but it is variant A in different clothes: one index probe per day.

### The index

```sql
CREATE INDEX "IX_Payments_ClientId_Dt"
    ON client."Payments" ("ClientId", "Dt") INCLUDE ("Amount");
```

Equality column first, range column second: a b-tree can use its columns as a range only after
the last equality, so the other order leaves `Dt` useless for seeking. `Amount` is included
rather than indexed because it is never a predicate, only a payload, and including it is what
makes the scan index-only.

The brief specifies four columns and no indexes, so it is worth being explicit that this one
is part of the answer rather than a convenience. On the same five million rows with only the
primary key present:

| | Wide, no index | Narrow, no index | Wide, with the index |
|---|---|---|---|
| **A** — range in the join | 781 ms | | 6.1 ms |
| **B** — `::date` in the join | 121 ms | | 3.8 ms |
| **C** — pre-aggregate, equality join | 149 ms | 141 ms | 2.2 ms |

Three things follow. The index is worth about 70× on its own, which is an order of magnitude
more than the largest gap between the three query shapes. Without it the window stops
mattering at all — C costs the same 140 ms for seven days as for eleven years, because there
is no such thing as reading only the window when every row has to be examined. And the ranking
between B and C inverts, both being dominated by the same sequential scan with the join
strategy as noise on top of it.

So the choice of query shape is only a real choice once the access path exists. A payments
table where "this client's payments" is a sequential scan is not a table that needs a better
query, and `A` remaining catastrophic in both columns is the one result that does not depend
on the index: its cost comes from evaluating the range once per day, which no index fixes.

## Type mapping

| Brief (T-SQL) | PostgreSQL | Why |
|---|---|---|
| `bigint` | `bigint` | Same |
| `datetime2(0)` | `timestamp(0)` | Both are *without* time zone. Not `timestamptz`: there is no time zone in the source data, and adding one would shift day boundaries by the session's offset — the exact thing this function is about |
| `money` | `numeric(19,4)` | **Not** PostgreSQL's `money`, whose input and output formats depend on the session's `lc_monetary` and whose fractional precision is a server setting rather than a property of the column. SQL Server's `money` is a fixed-point 19,4, so `numeric(19,4)` is the faithful equivalent |

SQL Server's own `money` is worth avoiding in new schemas too: intermediate results in division
truncate to four decimal places, which `decimal(19,4)` does not. The DDL keeps it because the
brief specifies it.

## At the edges

Every row here is an assertion in `03-tests-postgres.sql`.

| Case | Result | Note |
|---|---|---|
| The brief's two worked examples | Match exactly | Including 450 on 2022-01-05, which is two payments |
| `Sd > Ed` | No rows | Deliberate, not an error. `generate_series` yields nothing and the function inherits that |
| `Sd` or `Ed` is `NULL` | No rows | Same mechanism. The T-SQL version needs an explicit guard |
| `Sd = Ed` | Exactly one row | Off-by-one on an inclusive interval |
| A client with no payments in the window | Every day, all `0` | Not an empty result |
| An unknown `ClientId` | Every day, all `0` | Same case. The function answers about an interval, not about a client |
| A payment at `00:00:00` | Counts on that day | The half-open range includes the lower bound |
| A payment at `23:59:59` | Counts on that day | It excludes only the next midnight |
| A refund (negative `Amount`) | Nets off within its day | A sum, not a sum of positives |
| Ten-year interval | 3 653 consecutive days, no nulls | The brief's explicit hint |

## Dialect

Task 2 names no engine. Task 1 asks for "PostgreSQL or Microsoft SQL Server" and its
application is built on PostgreSQL, so that is what this is written and measured against. The
brief's vocabulary is SQL Server's — `datetime2(0)` exists only there — so a translation is
included as well.

`05-function-mssql.sql` has not been run; no SQL Server instance was available. The logic is
the same and the reasoning behind the shape transfers, since the cost difference between a
range join and an equality join is not dialect-specific, but the plans were not observed.

Three differences beyond syntax:

**No `GENERATE_SERIES` before SQL Server 2022.** The calendar comes from a tally source,
`sys.all_objects` cross-joined with itself. On 2022 and later the CTE collapses to
`FROM GENERATE_SERIES(0, DATEDIFF(DAY, @Sd, @Ed))`. In a real database the answer is neither:
a permanent, indexed `dbo.Calendar` table that every report shares.

**`TOP` needs guarding.** `DATEDIFF` on a reversed interval is negative and `TOP (-1)` raises;
a `NULL` parameter gives `TOP (NULL)`, which also raises. Both fold to `TOP (0)`, which is
legal and returns nothing — matching what PostgreSQL does for free.

**It must be an inline table-valued function.** `RETURNS TABLE AS RETURN (single select)` is
expanded into the calling query and optimised with it; the multi-statement form is an optimiser
barrier with a fixed cardinality estimate. The same reasoning makes the PostgreSQL function
`LANGUAGE sql` and `STABLE`: a plain SQL function whose body is one `SELECT` can be inlined by
the planner, while PL/pgSQL or `VOLATILE` would block it.

An inline TVF cannot contain `ORDER BY`; SQL Server reserves that for the caller. The
PostgreSQL version does order its output, the result being a calendar.

## What this does not do

**No per-client aggregate table.** At a volume where this is called constantly over long
intervals, the answer stops being a query and becomes a daily-totals table maintained by the
same transaction as the payment. The calendar spine would still be needed for the empty days.

**No partitioning.** Range-partitioning by month is what a payments table of real size gets,
and it would let the planner prune to the interval before the index is consulted.

**One client at a time,** because that is what was asked. A set of clients means crossing the
calendar with the client list and grouping by both; the shape does not change.
