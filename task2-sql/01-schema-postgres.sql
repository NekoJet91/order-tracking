-- Schema for task 2, PostgreSQL.
--
-- The brief describes the table in T-SQL types. The mapping and the reasoning behind each
-- choice are in README.md; the one that matters is Amount, which is numeric(19,4) rather
-- than PostgreSQL's money type.

CREATE SCHEMA IF NOT EXISTS client;

DROP TABLE IF EXISTS client."Payments";

CREATE TABLE client."Payments" (
    "Id"       bigserial     PRIMARY KEY,
    "ClientId" bigint        NOT NULL,
    "Dt"       timestamp(0)  NOT NULL,
    "Amount"   numeric(19,4) NOT NULL
);

-- The function's access path, and the reason its join predicate is written as a half-open
-- range rather than a cast to date. ClientId first because it is an equality, Dt second
-- because it is the range, Amount as an included payload so the aggregate can be answered
-- from the index without touching the heap.
CREATE INDEX "IX_Payments_ClientId_Dt"
    ON client."Payments" ("ClientId", "Dt") INCLUDE ("Amount");
