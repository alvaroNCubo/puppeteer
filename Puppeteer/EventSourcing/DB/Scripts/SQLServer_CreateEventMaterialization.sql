-- =============================================
-- Script: Crear tablas EventMaterialization y EventMaterializationBuffer para SQL Server
-- Paper 5 / claim 4 (firmado 2026-05-12 PM)
-- Descripcion: Marker DSL .Metadata.Materialize(destination) — el actor primary
--              escribe (DiaryId, ReactionId, Destination, Timestamp); un delivery
--              worker external lee y entrega. Por-actor-por-construccion
--              (cross-ref project_actor_per_db_principle.md).
-- =============================================

-- Tabla definitiva de markers de materializacion
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventMaterialization')
BEGIN
    CREATE TABLE EventMaterialization
    (
        DiaryId BIGINT NOT NULL,
        ReactionId INT NOT NULL,
        Destination NVARCHAR(255) NOT NULL,
        Timestamp DATETIME NOT NULL,
        CONSTRAINT PK_EventMaterialization PRIMARY KEY (DiaryId, Destination)
    );

    CREATE NONCLUSTERED INDEX IX_EventMaterialization_Destination
    ON EventMaterialization (Destination);

    PRINT 'Tabla EventMaterialization creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La tabla EventMaterialization ya existe.';
END
GO

-- Tabla de buffer temporal para staging
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventMaterializationBuffer')
BEGIN
    CREATE TABLE EventMaterializationBuffer
    (
        DiaryId BIGINT NOT NULL,
        ReactionId INT NOT NULL,
        Destination NVARCHAR(255) NOT NULL,
        Timestamp DATETIME NOT NULL
    );

    CREATE NONCLUSTERED INDEX IX_EventMaterializationBuffer_Destination
    ON EventMaterializationBuffer (Destination);

    PRINT 'Tabla EventMaterializationBuffer creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La tabla EventMaterializationBuffer ya existe.';
END
GO
