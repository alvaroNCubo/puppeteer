-- =============================================
-- Script: Create EventElision and EventElisionBuffer tables for MySQL
-- Description: Implements a transactional staging/buffer pattern
--              for bulk elision marking operations
-- =============================================

-- Final elision table
CREATE TABLE IF NOT EXISTS EventElision
(
    DairyId BIGINT NOT NULL,
    ReactionId INT NOT NULL,
    Timestamp DATETIME(3) NOT NULL,
    INDEX IX_EventElision_DairyId (DairyId)
) ENGINE=InnoDB CHARSET=utf8;

-- Temporary buffer table for elision staging
-- Used to accumulate elisions before the transactional commit
CREATE TABLE IF NOT EXISTS EventElisionBuffer
(
    DairyId BIGINT NOT NULL,
    ReactionId INT NOT NULL,
    Timestamp DATETIME(3) NOT NULL,
    INDEX IX_EventElisionBuffer_DairyId (DairyId)
) ENGINE=InnoDB CHARSET=utf8;

-- =============================================
-- Script: Migration of existing data
-- Description: Migrates records with Skip=1 from existing Dairy tables
--              to the new EventElision table
-- =============================================

-- Note: This script must be run for each existing actor/Dairy table
-- Replace {ACTOR_NAME} with the actual actor name

-- Migration example for a specific table:
/*
INSERT INTO EventElision (DairyId, ReactionId, Timestamp)
SELECT id, 0, NOW()
FROM {ACTOR_NAME}
WHERE Skip = 1
AND NOT EXISTS (
    SELECT 1 FROM EventElision
    WHERE DairyId = {ACTOR_NAME}.id
);

-- ReactionId is set to 0 for historical data predating
-- the Reactions system, indicating "manual or legacy elision"
*/
