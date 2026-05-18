-- =============================================
-- Script: Crear tablas EventElision y EventElisionBuffer para SQL Server
-- Descripción: Implementa patrón de staging/buffer transaccional
--              para operaciones masivas de marcado de elisiones
-- =============================================

-- Tabla definitiva de elisiones
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

    PRINT 'Tabla EventElision creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La tabla EventElision ya existe.';
END
GO

-- Tabla de buffer temporal para staging de elisiones
-- Se usa para acumular elisiones antes del commit transaccional
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

    PRINT 'Tabla EventElisionBuffer creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La tabla EventElisionBuffer ya existe.';
END
GO

-- =============================================
-- Script: Migración de datos existentes
-- Descripción: Migra registros con Skip=1 de tablas Dairy existentes
--              a la nueva tabla EventElision
-- =============================================

-- Nota: Este script debe ejecutarse para cada actor/tabla Dairy existente
-- Reemplazar {NOMBRE_ACTOR} con el nombre real del actor

-- Ejemplo de migración para una tabla específica:
/*
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{NOMBRE_ACTOR}')
BEGIN
    INSERT INTO EventElision (DairyId, ReactionId, Timestamp)
    SELECT id, 0, GETDATE()
    FROM {NOMBRE_ACTOR} WITH (NOLOCK)
    WHERE Skip = 1
    AND NOT EXISTS (
        SELECT 1 FROM EventElision
        WHERE DairyId = {NOMBRE_ACTOR}.id
    );

    PRINT 'Migración completada para {NOMBRE_ACTOR}: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' registros migrados.';
END
*/

-- El ReactionId se establece en 0 para datos históricos previos
-- al sistema de Reactions, indicando "elision manual o legacy"
