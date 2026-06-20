-- =============================================================================
-- Migration: legacy `Ip` / `[User]` journal columns -> Playbill schema "RestApi"
-- (SQL Server / Azure SQL Edge variant)
-- =============================================================================
-- Run once per actor database, BEFORE deploying the Fase 1 code change that
-- drops the `Ip` and `[User]` columns from the journal table.
--
-- Substitute the placeholder <ActorName> with the actor's table name (each
-- actor lives in its own table per `project_actor_per_db_principle.md`).
-- Run inside the actor's database (USE <ActorDb> beforehand).
--
-- The migration is IDEMPOTENT — re-running on a partially-migrated database
-- is safe.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Step 1: Create Playbill tables (idempotent)
-- -----------------------------------------------------------------------------
IF OBJECT_ID('PlaybillSchemas', 'U') IS NULL
BEGIN
    CREATE TABLE PlaybillSchemas (
        SchemaName    VARCHAR(64)    NOT NULL,
        Declarations  VARCHAR(2000)  NOT NULL,
        CreatedAt     DATETIME       NOT NULL,
        CONSTRAINT PK_PlaybillSchemas PRIMARY KEY (SchemaName)
    );
END;

IF OBJECT_ID('PlaybillRecords', 'U') IS NULL
BEGIN
    CREATE TABLE PlaybillRecords (
        EntryId               BIGINT         NOT NULL,
        SchemaName            VARCHAR(64)    NOT NULL,
        SerializedParameters  VARCHAR(2000)  NOT NULL,
        CONSTRAINT PK_PlaybillRecords PRIMARY KEY (EntryId)
    );
    CREATE INDEX IX_PlaybillRecords_SchemaName ON PlaybillRecords (SchemaName);
END;


-- -----------------------------------------------------------------------------
-- Step 2: Register the "RestApi" schema (idempotent)
-- -----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM PlaybillSchemas WHERE SchemaName = 'RestApi')
BEGIN
    INSERT INTO PlaybillSchemas (SchemaName, Declarations, CreatedAt)
    VALUES ('RestApi', 'In,ip:string,In,user:string', GETDATE());
END;


-- -----------------------------------------------------------------------------
-- Step 3: Migrate legacy journal Ip/[User] columns to PlaybillRecords
--
-- For every row with non-null Ip OR [User], create a PlaybillRecord with schema
-- 'RestApi' and the same EntryId. The serialized parameters use V2 Parameters
-- wire format: `declarations|arguments` (pipe-separated, comma-separated args,
-- single-quoted strings with internal quotes escaped via backslash).
--
-- IMPORTANT: Puppeteer's IN_MEMORY wire format (used by Playbill.WriteRecord
-- via Parameters.SerializeForTransport(IN_MEMORY)) escapes the embedded
-- apostrophe as `\'`, NOT as the SQL-standard `''`. The migration must produce
-- the same byte sequence; therefore the inner REPLACE replaces a single quote
-- with the two-char sequence `\'`. In T-SQL the literal `\'` is written as
-- '\''' (backslash + escaped-quote).
--
-- NULL coercion to preserve legacy sentinel semantics:
--   Ip     IS NULL  ->  '0.0.0.0'    (was IpAddress.DEFAULT.Ip)
--   [User] IS NULL  ->  'Anonymous'  (was UserInLog.ANONYMOUS.Id)
--
-- The NOT EXISTS clause skips rows whose EntryId already has a PlaybillRecord
-- (idempotent re-run safe — SQL Server has no INSERT IGNORE).
-- -----------------------------------------------------------------------------
INSERT INTO PlaybillRecords (EntryId, SchemaName, SerializedParameters)
SELECT
    j.id AS EntryId,
    'RestApi' AS SchemaName,
    'In,ip:string,In,user:string|' +
    '''' + REPLACE(ISNULL(j.Ip, '0.0.0.0'), '''', '\''') + '''' +
    ',' +
    '''' + REPLACE(ISNULL(j.[User], 'Anonymous'), '''', '\''') + '''' AS SerializedParameters
FROM [<ActorName>] j
WHERE (j.Ip IS NOT NULL OR j.[User] IS NOT NULL)
  AND NOT EXISTS (SELECT 1 FROM PlaybillRecords pr WHERE pr.EntryId = j.id);


-- -----------------------------------------------------------------------------
-- Step 4 (OPTIONAL): Drop legacy columns from the journal table
--
-- Run ONLY after:
--   (a) Production code is deployed with Fase 1 changes (stops writing Ip/User)
--   (b) You have verified PlaybillRecords contains all expected rows
--   (c) Any downstream consumers reading the legacy columns have been migrated
--
-- These ALTER TABLE operations are expensive on large journals. Schedule them
-- during a maintenance window.
-- -----------------------------------------------------------------------------
-- ALTER TABLE [<ActorName>] DROP COLUMN Ip;
-- ALTER TABLE [<ActorName>] DROP COLUMN [User];


-- -----------------------------------------------------------------------------
-- Verification queries (run manually after migration to confirm)
-- -----------------------------------------------------------------------------
-- SELECT COUNT(*) AS LegacyRowsWithIpOrUser FROM [<ActorName>]
--   WHERE Ip IS NOT NULL OR [User] IS NOT NULL;
-- SELECT COUNT(*) AS PlaybillRecordsForRestApi FROM PlaybillRecords
--   WHERE SchemaName = 'RestApi';
-- -- Both counts should match (modulo any pre-existing PlaybillRecords).
