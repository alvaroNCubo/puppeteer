-- =============================================
-- Script: Crear tablas EventMaterialization y EventMaterializationBuffer para MySQL
-- Paper 5 / claim 4 (firmado 2026-05-12 PM)
-- Descripcion: Marker DSL .Metadata.Materialize(destination) — el actor primary
--              escribe (DiaryId, ReactionId, Destination, Timestamp); un delivery
--              worker external lee y entrega. Por-actor-por-construccion
--              (cross-ref project_actor_per_db_principle.md).
-- =============================================

-- Tabla definitiva de markers de materializacion
CREATE TABLE IF NOT EXISTS EventMaterialization
(
    DiaryId BIGINT NOT NULL,
    ReactionId INT NOT NULL,
    Destination VARCHAR(255) NOT NULL,
    Timestamp DATETIME(3) NOT NULL,
    PRIMARY KEY (DiaryId, Destination),
    INDEX IX_EventMaterialization_Destination (Destination)
) ENGINE=InnoDB CHARSET=utf8;

-- Tabla de buffer temporal para staging
CREATE TABLE IF NOT EXISTS EventMaterializationBuffer
(
    DiaryId BIGINT NOT NULL,
    ReactionId INT NOT NULL,
    Destination VARCHAR(255) NOT NULL,
    Timestamp DATETIME(3) NOT NULL,
    INDEX IX_EventMaterializationBuffer_Destination (Destination)
) ENGINE=InnoDB CHARSET=utf8;
