-- =============================================
-- Script: Crear tabla ExposeData para MySQL
-- Descripción: Almacena los valores JSON generados por el comando expose
--              Solo los eventos que ejecutan expose tendrán un registro aquí
-- =============================================

-- Tabla para almacenar datos del expose
CREATE TABLE IF NOT EXISTS ExposeData
(
    DairyId BIGINT NOT NULL PRIMARY KEY,
    ExposeJson TEXT NOT NULL,
    INDEX IX_ExposeData_DairyId (DairyId)
) ENGINE=InnoDB CHARSET=utf8mb4;

-- =============================================
-- Notas de uso:
-- =============================================
-- 1. Esta tabla es OPCIONAL - solo contiene registros para eventos con expose
-- 2. DairyId referencia al id del evento en la tabla principal del actor
-- 3. ExposeJson contiene el JSON serializado con los valores del expose
-- 4. La fecha/hora del evento se obtiene desde la tabla Dairy principal (OccurredAt)
-- 5. Se usa utf8mb4 para soportar caracteres especiales en JSON
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
-- SELECT ExposeJson FROM ExposeData WHERE DairyId = 123;
--
-- Obtener eventos con expose en un rango de tiempo:
-- SELECT d.id, d.Script, ed.ExposeJson
-- FROM {ActorName} d
-- INNER JOIN ExposeData ed ON d.id = ed.DairyId
-- WHERE d.OccurredAt BETWEEN '2025-01-01' AND '2025-12-31';
--
-- Contar cuántos eventos tienen expose:
-- SELECT COUNT(*) FROM ExposeData;
-- =============================================
