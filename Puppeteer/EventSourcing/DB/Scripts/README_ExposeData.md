# Scripts de Base de Datos para ExposeData

## Descripción

Estos scripts crean la tabla `ExposeData` necesaria para almacenar los valores JSON generados por el comando `expose` en Puppeteer.

## Archivos

- **SQLServer_CreateExposeData.sql**: Script para SQL Server
- **MySQL_CreateExposeData.sql**: Script para MySQL

## Estructura de la Tabla

```sql
ExposeData
├── DairyId (BIGINT, PRIMARY KEY)   -- Referencia al id del evento
└── ExposeJson (TEXT/NVARCHAR(MAX)) -- JSON con los valores del expose
```

**Nota**: La fecha/hora del evento se obtiene desde la tabla Dairy principal (campo OccurredAt) mediante el DairyId.

## Características

1. **Tabla opcional**: Solo contiene registros para eventos que ejecutan `expose`
2. **Pureza del evento**: Mantiene separados los datos del expose de la tabla principal
3. **Performance**: Índice en DairyId para búsquedas rápidas
4. **Idempotente**: Los scripts pueden ejecutarse múltiples veces sin error

## Uso

### SQL Server

```sql
-- Ejecutar en la base de datos del actor
USE [NombreBaseDatos]
GO

-- Ejecutar el script
:r SQLServer_CreateExposeData.sql
```

### MySQL

```bash
# Ejecutar desde consola
mysql -u usuario -p nombre_base_datos < MySQL_CreateExposeData.sql

# O desde MySQL Workbench/cliente
SOURCE MySQL_CreateExposeData.sql;
```

## Ejemplo de Datos

Cuando un script ejecuta:
```
x = 42;
expose x 'total';
```

Se crea un registro en ExposeData:
```json
{
  "DairyId": 123,
  "ExposeJson": "{\"total\":42}"
}
```

Para obtener la fecha del evento, se hace JOIN con la tabla Dairy:
```sql
SELECT d.OccurredAt, ed.ExposeJson
FROM ActorName d
INNER JOIN ExposeData ed ON d.id = ed.DairyId
WHERE ed.DairyId = 123;
```

## Consultas Útiles

### Obtener expose de un evento específico
```sql
SELECT ExposeJson
FROM ExposeData
WHERE DairyId = 123;
```

### Eventos con expose en rango de tiempo
```sql
-- SQL Server
SELECT d.id, d.Script, ed.ExposeJson
FROM ActorName d
INNER JOIN ExposeData ed ON d.id = ed.DairyId
WHERE d.OccurredAt BETWEEN '2025-01-01' AND '2025-12-31';

-- MySQL
SELECT d.id, d.Script, ed.ExposeJson
FROM ActorName d
INNER JOIN ExposeData ed ON d.id = ed.DairyId
WHERE d.OccurredAt BETWEEN '2025-01-01' AND '2025-12-31';
```

### Contar eventos con expose
```sql
SELECT COUNT(*) as EventosConExpose
FROM ExposeData;
```

### Percentage de eventos con expose
```sql
-- SQL Server
SELECT
    (CAST(COUNT(ed.DairyId) AS FLOAT) / COUNT(d.id)) * 100 as PorcentajeConExpose
FROM ActorName d
LEFT JOIN ExposeData ed ON d.id = ed.DairyId;

-- MySQL
SELECT
    (COUNT(ed.DairyId) / COUNT(d.id)) * 100 as PorcentajeConExpose
FROM ActorName d
LEFT JOIN ExposeData ed ON d.id = ed.DairyId;
```

## Mantenimiento

### Limpieza de datos antiguos
```sql
-- Eliminar expose data de eventos más antiguos que 1 año
-- SQL Server
DELETE ed
FROM ExposeData ed
INNER JOIN ActorName d ON ed.DairyId = d.id
WHERE d.OccurredAt < DATEADD(year, -1, GETDATE());

-- MySQL
DELETE ed
FROM ExposeData ed
INNER JOIN ActorName d ON ed.DairyId = d.id
WHERE d.OccurredAt < DATE_SUB(NOW(), INTERVAL 1 YEAR);
```

### Backup solo de ExposeData
```sql
-- SQL Server
SELECT * INTO ExposeData_Backup FROM ExposeData;

-- MySQL
CREATE TABLE ExposeData_Backup AS SELECT * FROM ExposeData;
```

## Migración

Si necesitas migrar ExposeData existente de una tabla principal:

```sql
-- Este script NO aplica para instalaciones nuevas
-- Solo si previamente guardaste expose en una columna de la tabla principal

-- SQL Server
INSERT INTO ExposeData (DairyId, ExposeJson)
SELECT id, ExposeDataColumn
FROM ActorName
WHERE ExposeDataColumn IS NOT NULL;

-- MySQL
INSERT INTO ExposeData (DairyId, ExposeJson)
SELECT id, ExposeDataColumn
FROM ActorName
WHERE ExposeDataColumn IS NOT NULL;
```

## Notas de Implementación

1. **Performance**: La tabla usa PRIMARY KEY en DairyId para garantizar unicidad y performance
2. **JSON Storage**:
   - SQL Server usa NVARCHAR(MAX) para soportar JSON de cualquier tamaño
   - MySQL usa TEXT con charset utf8mb4 para soportar caracteres especiales
3. **OccurredAt**: Se obtiene desde la tabla Dairy principal mediante DairyId, evitando redundancia
4. **Índice**: El índice en DairyId permite JOINs eficientes con la tabla principal

## Troubleshooting

### Error: "Table already exists"
Los scripts son idempotentes. Si la tabla ya existe, simplemente imprimen un mensaje informativo.

### Error: "Cannot create PRIMARY KEY"
Si DairyId tiene duplicados, la creación fallará. Esto indica un problema en los datos existentes.

### Performance: JOINs lentos
Si los JOINs con ExposeData son lentos:
1. Verificar que el índice IX_ExposeData_DairyId existe
2. Actualizar estadísticas de la tabla
3. Considerar agregar índices adicionales según patrones de consulta
