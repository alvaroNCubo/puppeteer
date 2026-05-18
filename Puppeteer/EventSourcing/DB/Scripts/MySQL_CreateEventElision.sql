-- =============================================
-- Script: Crear tablas EventElision y EventElisionBuffer para MySQL
-- Descripción: Implementa patrón de staging/buffer transaccional
--              para operaciones masivas de marcado de elisiones
-- =============================================

-- Tabla definitiva de elisiones
CREATE TABLE IF NOT EXISTS EventElision
(
    DairyId BIGINT NOT NULL,
    ReactionId INT NOT NULL,
    Timestamp DATETIME(3) NOT NULL,
    INDEX IX_EventElision_DairyId (DairyId)
) ENGINE=InnoDB CHARSET=utf8;

-- Tabla de buffer temporal para staging de elisiones
-- Se usa para acumular elisiones antes del commit transaccional
CREATE TABLE IF NOT EXISTS EventElisionBuffer
(
    DairyId BIGINT NOT NULL,
    ReactionId INT NOT NULL,
    Timestamp DATETIME(3) NOT NULL,
    INDEX IX_EventElisionBuffer_DairyId (DairyId)
) ENGINE=InnoDB CHARSET=utf8;

-- =============================================
-- Script: Migración de datos existentes
-- Descripción: Migra registros con Skip=1 de tablas Dairy existentes
--              a la nueva tabla EventElision
-- =============================================

-- Nota: Este script debe ejecutarse para cada actor/tabla Dairy existente
-- Reemplazar {NOMBRE_ACTOR} con el nombre real del actor

-- Ejemplo de migración para una tabla específica:
/*
INSERT INTO EventElision (DairyId, ReactionId, Timestamp)
SELECT id, 0, NOW()
FROM {NOMBRE_ACTOR}
WHERE Skip = 1
AND NOT EXISTS (
    SELECT 1 FROM EventElision
    WHERE DairyId = {NOMBRE_ACTOR}.id
);

-- El ReactionId se establece en 0 para datos históricos previos
-- al sistema de Reactions, indicando "elision manual o legacy"
*/
