-- =============================================
-- Script: Crear tabla ExposeData para SQL Server
-- Descripción: Almacena los valores JSON generados por el comando expose
--              Solo los eventos que ejecutan expose tendrán un registro aquí
-- =============================================

-- Tabla para almacenar datos del expose
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ExposeData')
BEGIN
    CREATE TABLE ExposeData
    (
        DairyId BIGINT NOT NULL PRIMARY KEY,
        ExposeJson NVARCHAR(MAX) NOT NULL
    );

    CREATE NONCLUSTERED INDEX IX_ExposeData_DairyId
    ON ExposeData (DairyId);

    PRINT 'Tabla ExposeData creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La tabla ExposeData ya existe.';
END
GO

-- =============================================
-- Notas de uso:
-- =============================================
-- 1. Esta tabla es OPCIONAL - solo contiene registros para eventos con expose
-- 2. DairyId referencia al id del evento en la tabla principal del actor
-- 3. ExposeJson contiene el JSON serializado con los valores del expose
-- 4. La fecha/hora del evento se obtiene desde la tabla Dairy principal (OccurredAt)
--
-- Ejemplo de registro:
-- DairyId: 123
-- ExposeJson: {"total":42,"subtotal":21}
--
-- =============================================
-- Consultas de ejemplo:
-- =============================================
--
-- Obtener ExposeData de un evento específico:
-- SELECT ExposeJson FROM ExposeData WHERE DairyId = 123
--
-- Obtener eventos con expose en un rango de tiempo:
-- SELECT d.id, d.Script, ed.ExposeJson
-- FROM {ActorName} d
-- INNER JOIN ExposeData ed ON d.id = ed.DairyId
-- WHERE d.OccurredAt BETWEEN '2025-01-01' AND '2025-12-31'
--
-- Contar cuántos eventos tienen expose:
-- SELECT COUNT(*) FROM ExposeData
-- =============================================
