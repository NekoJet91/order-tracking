-- SQL Server parity version of client.get_daily_payment_totals.
--
-- NOT VERIFIED.

-- --------------------------------------------------------------------------------- schema --

IF SCHEMA_ID('client') IS NULL
    EXEC ('CREATE SCHEMA client');
GO

IF OBJECT_ID('client.Payments', 'U') IS NULL
CREATE TABLE client.Payments
(
    Id       bigint       IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
    ClientId bigint       NOT NULL,
    Dt       datetime2(0) NOT NULL,
    Amount   money        NOT NULL
);
GO

-- Same access path as the PostgreSQL index: equality column first, range column second,
-- the aggregated column as an included payload so the aggregate can be answered from the
-- index alone.
IF INDEXPROPERTY(OBJECT_ID('client.Payments'), 'IX_Payments_ClientId_Dt', 'IndexID') IS NULL
CREATE NONCLUSTERED INDEX IX_Payments_ClientId_Dt
    ON client.Payments (ClientId, Dt) INCLUDE (Amount);
GO

-- ------------------------------------------------------------------------------- function --

CREATE OR ALTER FUNCTION client.GetDailyPaymentTotals
(
    @ClientId bigint,
    @Sd       date,
    @Ed       date
)
RETURNS TABLE
AS
-- Inline, not multi-statement. `RETURNS TABLE AS RETURN (one select)` is expanded into the
-- calling query and optimised as part of it; the multi-statement form
-- (`RETURNS @t TABLE (...) AS BEGIN ... END`) is an optimiser barrier with a fixed cardinality
-- estimate, and is the direct counterpart of writing this in PL/pgSQL with a loop.
RETURN
(
    WITH Numbers AS
    (
        -- No GENERATE_SERIES before SQL Server 2022, so the calendar comes from a tally
        -- source. sys.all_objects cross-joined with itself yields millions of rows, which
        -- covers any interval that could be asked for. On 2022 and later this whole CTE
        -- collapses to `FROM GENERATE_SERIES(0, DATEDIFF(DAY, @Sd, @Ed))`.
        --
        -- In a production database the right answer is neither: a permanent dbo.Calendar
        -- table, indexed, which every report in the system can share.
        SELECT TOP (CASE WHEN @Sd IS NULL OR @Ed IS NULL OR @Ed < @Sd
                         THEN 0
                         -- TOP (-1) raises, and TOP (NULL) raises, so both are folded to
                         -- TOP (0) above, which is legal and returns nothing.
                         ELSE DATEDIFF(DAY, @Sd, @Ed) + 1
                    END)
               n = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1
        FROM       sys.all_objects AS a
        CROSS JOIN sys.all_objects AS b
    ),
    Calendar AS
    (
        SELECT Day = DATEADD(DAY, n, @Sd) FROM Numbers
    ),
    Paid AS
    (
        -- The range is applied once over the whole interval, in the WHERE clause and on the
        -- raw column, so it is one index seek. The upper bound is the midnight that starts
        -- the day after @Ed: Dt carries a time, and `<= @Ed` would lose every payment made
        -- during the last day of the interval.
        SELECT PaidOn = CAST(p.Dt AS date),
               Total  = SUM(p.Amount)
        FROM   client.Payments AS p
        WHERE  p.ClientId = @ClientId
          AND  p.Dt >= @Sd
          AND  p.Dt <  DATEADD(DAY, 1, @Ed)
        GROUP BY CAST(p.Dt AS date)
    )
    SELECT   c.Day AS Dt,
             [Сумма] = ISNULL(pd.Total, 0)
    FROM     Calendar AS c
    LEFT JOIN Paid AS pd ON pd.PaidOn = c.Day
);
GO

-- ---------------------------------------------------------------------------------- usage --

-- The brief's two worked examples.
-- SELECT * FROM client.GetDailyPaymentTotals(1, '2022-01-02', '2022-01-07') ORDER BY Dt;
-- SELECT * FROM client.GetDailyPaymentTotals(2, '2022-01-04', '2022-01-11') ORDER BY Dt;
--
-- An inline table-valued function has no ORDER BY of its own — SQL Server does not allow one
-- in a view or an inline TVF, because the caller is entitled to decide. Order in the caller.
