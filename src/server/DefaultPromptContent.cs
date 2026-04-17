namespace RevitMcp.Server
{
    /// <summary>
    /// Embedded default content for MCP prompt files.
    /// Written to %APPDATA%/KEI/AI/prompts/ on first access (S6 pattern).
    /// </summary>
    internal static class DefaultPromptContent
    {
        internal const string DbKnowledge = @"# KEI Database Knowledge Base

## Two Databases

KEI uses two separate SQLite databases:

1. **Project DB** — per-project, at `%APPDATA%/KEI/Database/Projects/{name}.db`
2. **Master Equipment DB** — shared catalog, at `%APPDATA%/KEI/Database/KEI_Master_Equipment.db`

The `query_kei_database` tool queries the **Project DB** only.
Master Equipment DB is not directly queryable via MCP tools.

---

## Project DB Schema

### Elements — All Revit elements tracked by KEI
```sql
CREATE TABLE Elements (
    ElementId           INTEGER PRIMARY KEY, -- Revit ElementId
    FamilyName          TEXT,
    FamilyType          TEXT,
    Category            TEXT,    -- e.g. ""Pipes"", ""Pipe Fittings"", ""Mechanical Equipment""
    RawSizeString       TEXT,    -- Raw Revit size parameter value
    Size                TEXT,    -- Parsed/normalized size
    Length              REAL,    -- In feet (Revit internal unit)
    FittingType         TEXT,    -- NULL for non-fittings
    Angle               REAL,
    Slope               REAL,
    Location            TEXT,    -- JSON: {X,Y,Z} in feet
    Phase               TEXT,
    HasSuperComponent   INTEGER, -- 1 if nested under another element
    IsSleeveOrOpening   INTEGER,
    MinX REAL, MinY REAL, MinZ REAL,  -- Bounding box
    MaxX REAL, MaxY REAL, MaxZ REAL,
    AxisX REAL, AxisY REAL, AxisZ REAL -- Direction vector
);
```

### Systems — MEP piping/duct systems
```sql
CREATE TABLE Systems (
    SystemId    INTEGER PRIMARY KEY,
    SystemName  TEXT    -- e.g. ""1.07_WW_PUMP_TK07A/B""
);
```

### ElementSystems — Many-to-many link (element <-> system)
```sql
CREATE TABLE ElementSystems (
    ElementId   INTEGER,  -- FK -> Elements
    SystemId    INTEGER   -- FK -> Systems
);
```

### SupplyItemsCache — Denormalized supply view (Vietnamese field names)
```sql
CREATE TABLE SupplyItemsCache (
    ElementId           INTEGER PRIMARY KEY,
    TenVatTu            TEXT,    -- Material/item name
    KichThuoc           TEXT,    -- Display size (formatted)
    VatLieu             TEXT,    -- Material type
    TieuChuan           TEXT,    -- Standard (e.g. ""TCVN"", ""JIS"")
    DonVi               TEXT,    -- Unit (""m"", ""cai"", ""bo"")
    SoLuong             REAL,    -- Quantity
    Phase               TEXT,
    GhiChu              TEXT,    -- Notes/remarks
    IsEquipment         INTEGER, -- 1 if this is equipment
    TagNumber           TEXT,    -- Equipment tag (e.g. ""WP01.1-01"")
    ProjectEquipmentId  INTEGER,
    LastUpdated         TEXT,
    EquipmentSpecsSummary TEXT,  -- One-line specs summary
    EquipmentArea       TEXT     -- Area/zone code
);
```

### ChangeQueue — Real-time change tracking from Revit
```sql
CREATE TABLE ChangeQueue (
    QueueId         INTEGER PRIMARY KEY AUTOINCREMENT,
    ElementId       INTEGER,
    ChangeType      TEXT,    -- ""Added"", ""Modified"", ""Deleted""
    Timestamp       TEXT,
    ChangeData      TEXT,    -- JSON payload
    UserId          TEXT,
    MachineName     TEXT,
    SourceSessionId TEXT,
    LocalIP         TEXT,
    PublicIP        TEXT,
    SyncStatus      INTEGER,
    IsProcessed     INTEGER  -- 0 = pending, 1 = done
);
```

### Connectors — Pipe/duct connector geometry and hydraulics
```sql
CREATE TABLE Connectors (
    ConnectorId     INTEGER PRIMARY KEY AUTOINCREMENT,
    OwnerElementId  INTEGER,  -- FK -> Elements
    PositionX REAL, PositionY REAL, PositionZ REAL,
    DirectionX REAL, DirectionY REAL, DirectionZ REAL,
    Diameter        REAL,     -- In feet
    Flow            REAL,
    Velocity        REAL,
    PressureDrop    REAL,
    LossCoefficient REAL,
    ConnectedToId   INTEGER   -- FK -> Connectors (peer)
);
```

### ProjectEquipmentTypes — Equipment type definitions for this project
```sql
CREATE TABLE ProjectEquipmentTypes (
    ProjectTypeId       INTEGER PRIMARY KEY AUTOINCREMENT,
    MasterEquipmentId   INTEGER,  -- FK -> Master DB
    ProjectTypeName     TEXT,     -- e.g. ""Bom nuoc thai (WP01.1)""
    NameVN              TEXT,
    NameEN              TEXT,
    SpecsVN             TEXT,
    SpecsEN             TEXT,
    Area                TEXT,     -- e.g. ""TK01"", ""TK09.1-A""
    Brand               TEXT,
    ProjectCapacity     TEXT,
    Unit                TEXT,
    OriginalTag         TEXT
);
```

### ProjectEquipments — Individual equipment instances
```sql
CREATE TABLE ProjectEquipments (
    ProjectEquipmentId  INTEGER PRIMARY KEY AUTOINCREMENT,
    ProjectTypeId       INTEGER,  -- FK -> ProjectEquipmentTypes
    TagNumber           TEXT,     -- e.g. ""WP01.1-01""
    RevitElementId      INTEGER,  -- FK -> Elements (NULL if not placed)
    Status              TEXT
);
```

### OperatingConditions — Equipment operating parameters
```sql
CREATE TABLE OperatingConditions (
    ConditionId     INTEGER PRIMARY KEY AUTOINCREMENT,
    ProjectTypeId   INTEGER,  -- FK -> ProjectEquipmentTypes
    ParameterName   TEXT,
    Value           TEXT,
    Unit            TEXT
);
```

### CalculationProfiles — Pipe sizing calculation results
```sql
CREATE TABLE CalculationProfiles (
    ProfileId       INTEGER PRIMARY KEY AUTOINCREMENT,
    ProfileName     TEXT,
    ProjectName     TEXT,
    InputsJson      TEXT,
    SelectedDN      TEXT,
    ResultsJson     TEXT,
    CreatedAt       TEXT,
    ModifiedAt      TEXT
);
```

### Railing tables (BOQ/path analysis)
```sql
CREATE TABLE RailingBOQCache (
    Id INTEGER PRIMARY KEY, ChainId TEXT, ElementIds TEXT,
    IbomCode TEXT, TenVatTu TEXT, KichThuoc TEXT, VatLieu TEXT,
    TieuChuan TEXT, DonVi TEXT, SoLuong REAL, GhiChu TEXT,
    TooltipFormula TEXT, TooltipNote TEXT, LastUpdated TEXT
);
CREATE TABLE RailingFamilyConfig (
    FamilyName TEXT PRIMARY KEY, AssignedType TEXT, LastUpdated TEXT
);
CREATE TABLE RailingPathData (
    ElementId INTEGER PRIMARY KEY, PathDataJson TEXT,
    TotalLength REAL, CurveCount INTEGER, LastUpdated TEXT
);
```

### Support tables (pipe supports BOQ)
```sql
CREATE TABLE SupportBOQCache (
    Id INTEGER PRIMARY KEY, TenVatTu TEXT, KichThuoc TEXT,
    VatLieu TEXT, TieuChuan TEXT, DonVi TEXT, SoLuong REAL,
    GhiChu TEXT, ElementIds TEXT, LastUpdated TEXT
);
CREATE TABLE SupportComponentData (
    Id INTEGER PRIMARY KEY, ElementId INTEGER, ParentElementId INTEGER,
    ComponentType TEXT, ProfileSize TEXT, Material TEXT,
    LengthMm REAL, PipeDN TEXT
);
```

### Other tables
```sql
CREATE TABLE ElementHighlights (
    ElementId INTEGER PRIMARY KEY, HighlightColor TEXT
);
CREATE TABLE SleeveOpeningDetails (
    ElementId INTEGER PRIMARY KEY,
    Mark TEXT, ItemType TEXT, Location TEXT, Function TEXT,
    ElevationFt REAL, ElevationMm REAL, ElevationDisplay TEXT,
    Material TEXT, DiameterFt REAL, DiameterMm REAL,
    TotalLengthFt REAL, TotalLengthMm REAL,
    HostThicknessFt REAL, HostThicknessMm REAL,
    T1Ft REAL, T1Mm REAL, T2Ft REAL, T2Mm REAL,
    WidthFt REAL, WidthMm REAL, HeightFt REAL, HeightMm REAL
);
```

---

## Master Equipment DB Schema (separate file: KEI_Master_Equipment.db)

```sql
CREATE TABLE MasterEquipments (
    MasterEquipmentId   INTEGER PRIMARY KEY AUTOINCREMENT,
    CommonName          TEXT,
    ModelNumber         TEXT,
    Manufacturer        TEXT,
    PrimaryCategory     TEXT,
    SecondaryCategory   TEXT,
    Origin              TEXT,
    TypeCode            TEXT,     -- e.g. ""WP"", ""DP"", ""CS""
    HydraulicRole       TEXT NOT NULL, -- ""EnergySource"" or ""InLineComponent""
    Description         TEXT,
    Fabricator          TEXT,
    SpecFields          TEXT,     -- JSON array of spec field names
    UNIQUE(Manufacturer, ModelNumber)
);
CREATE TABLE EquipmentParameters (
    ParameterId         INTEGER PRIMARY KEY AUTOINCREMENT,
    MasterEquipmentId   INTEGER,  -- FK -> MasterEquipments
    Category            TEXT DEFAULT 'Physical',
    ParameterType       TEXT,     -- DesignFlowRate, DesignHead, RatedPower, etc.
    DataType            TEXT DEFAULT 'Text',
    Value               TEXT,
    Unit                TEXT,
    Condition           TEXT
);
CREATE TABLE MasterNozzles (
    MasterNozzleId      INTEGER PRIMARY KEY AUTOINCREMENT,
    MasterEquipmentId   INTEGER,
    Name                TEXT,
    NominalDiameter     REAL,
    ConnectionType      TEXT,
    ConnectionStandard  TEXT,
    FlowDirection       TEXT,
    DesignFlowRate      REAL,
    DesignPressure      REAL,
    FlowRateUnit        TEXT,
    PressureUnit        TEXT
);
CREATE TABLE UnitRegistry (
    ParameterType       TEXT PRIMARY KEY,
    Category            TEXT,
    DataType            TEXT,
    StorageUnit         TEXT,
    DisplayUnits        TEXT,
    DefaultDisplayUnit  TEXT,
    Description         TEXT
);
CREATE TABLE ProjectUsageLog (
    LogId               INTEGER PRIMARY KEY AUTOINCREMENT,
    MasterEquipmentId   INTEGER,
    ProjectName         TEXT,
    FirstUsedTimestamp  TEXT,
    UNIQUE(MasterEquipmentId, ProjectName)
);
```

---

## Common Mistakes & Gotchas

### 1. SystemName is NOT in Elements table
```sql
-- WRONG:
SELECT SystemName FROM Elements WHERE ElementId = 12345

-- RIGHT:
SELECT e.*, s.SystemName
FROM Elements e
JOIN ElementSystems es ON e.ElementId = es.ElementId
JOIN Systems s ON es.SystemId = s.SystemId
WHERE e.ElementId = 12345
```

### 2. One element can belong to multiple systems
An element may appear in ElementSystems multiple times. Use DISTINCT or
GROUP BY when counting elements:
```sql
SELECT DISTINCT e.ElementId, e.Category
FROM Elements e
JOIN ElementSystems es ON e.ElementId = es.ElementId
JOIN Systems s ON es.SystemId = s.SystemId
WHERE s.SystemName LIKE '1.07_WW%'
```

### 3. System names in Revit vs DB
Revit may show system name with suffix like "" N"" (supply) or "" R"" (return).
The DB stores the base name. Use LIKE for fuzzy matching:
```sql
WHERE s.SystemName LIKE '1.07_WW_PUMP%'
```

### 4. Use SupplyItemsCache for supply/BOQ queries
SupplyItemsCache is the denormalized view with formatted Vietnamese field
names. Prefer it over raw Elements for supply-related queries:
```sql
SELECT TenVatTu, KichThuoc, VatLieu, DonVi, SUM(SoLuong)
FROM SupplyItemsCache
WHERE IsEquipment = 0
GROUP BY TenVatTu, KichThuoc, VatLieu, DonVi
```

### 5. Equipment queries span two tables
For equipment with tags and specs:
```sql
SELECT pt.ProjectTypeName, pt.Area, pe.TagNumber, pt.SpecsVN
FROM ProjectEquipmentTypes pt
JOIN ProjectEquipments pe ON pt.ProjectTypeId = pe.ProjectTypeId
ORDER BY pt.Area, pe.TagNumber
```

### 6. Master Equipment is in a SEPARATE database
`query_kei_database` queries the Project DB only.
MasterEquipments, EquipmentParameters, MasterNozzles are in
KEI_Master_Equipment.db — a different file not queryable via this tool.

### 7. Lengths are in feet (Revit internal unit)
Elements.Length is in feet. Multiply by 304.8 for millimeters:
```sql
SELECT ElementId, ROUND(Length * 304.8, 1) AS LengthMm
FROM Elements WHERE Category = 'Pipes'
```
Connectors.Diameter is also in feet.

### 8. ChangeQueue: filter by IsProcessed
```sql
-- Pending changes only:
SELECT * FROM ChangeQueue WHERE IsProcessed = 0 ORDER BY Timestamp DESC

-- All recent changes:
SELECT * FROM ChangeQueue ORDER BY Timestamp DESC LIMIT 50
```

### 9. Connectors has NO SystemName column
Connectors stores geometry and hydraulics only. To find which system a
connector's element belongs to, JOIN through Elements -> ElementSystems -> Systems:
```sql
SELECT c.*, s.SystemName
FROM Connectors c
JOIN Elements e ON c.OwnerElementId = e.ElementId
JOIN ElementSystems es ON e.ElementId = es.ElementId
JOIN Systems s ON es.SystemId = s.SystemId
```
";

        internal const string ToolGuide = @"# KEI MCP Tool Guide

## Priority Rules (MUST follow this order)

1. **query_kei_database** (SQL) — FIRST CHOICE
   Fast, read-only, safe. Covers 80% of questions.
   Use presets for common queries: overview, schema, systems, categories.

2. **Purpose-built MCP tools** — SECOND CHOICE
   - get_selected_elements — currently selected in Revit
   - ai_element_filter — find elements by parameter conditions
   - analyze_model_statistics — category counts, totals
   - get_material_quantities — material takeoff
   - detect_system_elements — MEP system analysis
   - get_current_view_info — active view metadata
   - analyze_sheet_layout — sheet/viewport info
   - export_room_data — room boundaries and properties
   - flow_sort_system — hydraulic flow sorting
   - read_kei_logs — application logs

3. **send_code_to_revit** (Roslyn) — LAST RESORT ONLY
   Compiles and executes arbitrary C# inside Revit.
   Risk of crash, data corruption, or side effects.
   Only when SQL + existing tools cannot accomplish the task.

## Quick Reference

| I want to...                           | Use                      |
|----------------------------------------|--------------------------|
| Count elements by category             | query_kei_database (SQL) |
| List MEP systems                       | query_kei_database: preset=systems |
| Find supply/BOQ items                  | query_kei_database -> SupplyItemsCache |
| Check equipment tags and specs         | query_kei_database -> ProjectEquipmentTypes JOIN ProjectEquipments |
| See pending changes                    | query_kei_database: preset=unprocessed |
| Get DB structure                       | query_kei_database: preset=schema |
| Find elements by Revit parameter       | ai_element_filter |
| Get selected elements detail           | get_selected_elements |
| Overall model statistics               | analyze_model_statistics |
| Material quantities                    | get_material_quantities |
| Sort flow in piping system             | flow_sort_system |
| Read application logs                  | read_kei_logs |
| Complex multi-step Revit API operation | send_code_to_revit (LAST RESORT) |

## Available Databases

query_kei_database defaults to auto-detect from active Revit document.
- `database: ""list""` — see all project DBs with sizes and activity
- `database: ""partial_name""` — fuzzy match (e.g. ""XC_SD"", ""LongDuc"")
- `database: ""auto""` — detect from active Revit document (default)

## SQL Tips

- **SELECT only** — no INSERT, UPDATE, or DELETE allowed
- **Default limit:** 100 rows. Use `limit` parameter to change.
- **Use presets** when possible — they are optimized and tested:
  `overview`, `schema`, `systems`, `categories`, `supply_sample`,
  `change_queue`, `unprocessed`, `recent_changes`
- **Vietnamese column names** in SupplyItemsCache: TenVatTu (name),
  KichThuoc (size), VatLieu (material), TieuChuan (standard),
  DonVi (unit), SoLuong (quantity), GhiChu (notes)
";
    }
}
