-- =============================================
-- Script: Create EventElision and EventElisionBuffer tables for SQL Server
-- Description: Implements a transactional staging/buffer pattern
--              for bulk elision marking operations
-- =============================================

-- Final elision table
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventElision')
BEGIN
    CREATE TABLE EventElision
    (
        DairyId BIGINT NOT NULL,
        ReactionId INT NOT NULL,
        Timestamp DATETIME NOT NULL
    );

    CREATE NONCLUSTERED INDEX IX_EventElision_DairyId
    ON EventElision (DairyId);

    PRINT 'EventElision table created successfully.';
END
ELSE
BEGIN
    PRINT 'EventElision table already exists.';
END
GO

-- Temporary buffer table for elision staging
-- Used to accumulate elisions before the transactional commit
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventElisionBuffer')
BEGIN
    CREATE TABLE EventElisionBuffer
    (
        DairyId BIGINT NOT NULL,
        ReactionId INT NOT NULL,
        Timestamp DATETIME NOT NULL
    );

    CREATE NONCLUSTERED INDEX IX_EventElisionBuffer_DairyId
    ON EventElisionBuffer (DairyId);

    PRINT 'EventElisionBuffer table created successfully.';
END
ELSE
BEGIN
    PRINT 'EventElisionBuffer table already exists.';
END
GO

-- =============================================
-- Script: Migration of existing data
-- Description: Migrates records with Skip=1 from existing Dairy tables
--              to the new EventElision table
-- =============================================

-- Note: This script must be run for each existing actor/Dairy table
-- Replace {ACTOR_NAME} with the actual actor name

-- Migration example for a specific table:
/*
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{ACTOR_NAME}')
BEGIN
    INSERT INTO EventElision (DairyId, ReactionId, Timestamp)
    SELECT id, 0, GETDATE()
    FROM {ACTOR_NAME} WITH (NOLOCK)
    WHERE Skip = 1
    AND NOT EXISTS (
        SELECT 1 FROM EventElision
        WHERE DairyId = {ACTOR_NAME}.id
    );

    PRINT 'Migration completed for {ACTOR_NAME}: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' records migrated.';
END
*/

-- ReactionId is set to 0 for historical data predating
-- the Reactions system, indicating "manual or legacy elision"
