-- =============================================================================
-- Migration: legacy `Ip` / `User` journal columns -> Playbill schema "RestApi"
-- =============================================================================
-- Run once per actor database, BEFORE deploying the Fase 1 code change that
-- drops the `Ip` and `User` columns from the journal table.
--
-- Substitute the placeholder <ActorName> with the actor's table name (each
-- actor lives in its own table per `project_actor_per_db_principle.md`).
-- Run inside the actor's database (USE <ActorDb> beforehand).
--
-- The migration is IDEMPOTENT — re-running on a partially-migrated database
-- is safe (INSERT IGNORE on schema + PK collision on records).
--
-- After running this, deploy the Fase 1 code (which stops writing to Ip/User).
-- After verifying production stability, optionally run Step 4 to drop the
-- legacy columns physically.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Step 1: Create Playbill tables (idempotent)
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS PlaybillSchemas (
    SchemaName    VARCHAR(64)    NOT NULL,
    Declarations  VARCHAR(2000)  NOT NULL,
    CreatedAt     DATETIME       NOT NULL,
    PRIMARY KEY (SchemaName)
) ENGINE=InnoDB CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS PlaybillRecords (
    EntryId               BIGINT         NOT NULL,
    SchemaName            VARCHAR(64)    NOT NULL,
    SerializedParameters  VARCHAR(2000)  NOT NULL,
    PRIMARY KEY (EntryId),
    INDEX IX_PlaybillRecords_SchemaName (SchemaName)
) ENGINE=InnoDB CHARSET=utf8mb4;


-- -----------------------------------------------------------------------------
-- Step 2: Register the "RestApi" schema (idempotent via INSERT IGNORE)
-- -----------------------------------------------------------------------------
INSERT IGNORE INTO PlaybillSchemas (SchemaName, Declarations, CreatedAt)
VALUES ('RestApi', 'In,ip:string,In,user:string', NOW());


-- -----------------------------------------------------------------------------
-- Step 3: Migrate legacy journal Ip/User columns to PlaybillRecords
--
-- For every row with non-null Ip OR User, create a PlaybillRecord with schema
-- 'RestApi' and the same EntryId. The serialized parameters use V2 Parameters
-- wire format: `declarations|arguments` (pipe-separated, comma-separated args,
-- single-quoted strings).
--
-- NULL coercion to preserve legacy sentinel semantics:
--   Ip   IS NULL  ->  '0.0.0.0'    (was IpAddress.DEFAULT.Ip)
--   User IS NULL  ->  'Anonymous'  (was UserInLog.ANONYMOUS.Id)
--
-- MySQL's QUOTE() escapes internal single quotes as `\'` (backslash + quote)
-- — matches Puppeteer's IN_MEMORY wire format used by Playbill.WriteRecord
-- (Parameters.SerializeForTransport(IN_MEMORY) escapes ' as \'). Values like
-- O'Brien become 'O\'Brien' in both producer and migration output.
--
-- INSERT IGNORE skips rows where the EntryId already has a PlaybillRecord
-- (idempotent re-run safe).
-- -----------------------------------------------------------------------------
INSERT IGNORE INTO PlaybillRecords (EntryId, SchemaName, SerializedParameters)
SELECT
    id AS EntryId,
    'RestApi' AS SchemaName,
    CONCAT(
        'In,ip:string,In,user:string|',
        QUOTE(COALESCE(Ip, '0.0.0.0')),
        ',',
        QUOTE(COALESCE(`User`, 'Anonymous'))
    ) AS SerializedParameters
FROM `<ActorName>`
WHERE Ip IS NOT NULL OR `User` IS NOT NULL;


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
-- ALTER TABLE `<ActorName>` DROP COLUMN Ip;
-- ALTER TABLE `<ActorName>` DROP COLUMN `User`;


-- -----------------------------------------------------------------------------
-- Verification queries (run manually after migration to confirm)
-- -----------------------------------------------------------------------------
-- SELECT COUNT(*) AS LegacyRowsWithIpOrUser FROM `<ActorName>`
--   WHERE Ip IS NOT NULL OR `User` IS NOT NULL;
-- SELECT COUNT(*) AS PlaybillRecordsForRestApi FROM PlaybillRecords
--   WHERE SchemaName = 'RestApi';
-- -- Both counts should match (modulo any pre-existing PlaybillRecords).
