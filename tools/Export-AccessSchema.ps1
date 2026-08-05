<#
.SYNOPSIS
  Dumps the full schema of a JET/ACE (.mdb) database to CSV.

.DESCRIPTION
  Stage 2 of the SMSLIBRE port project. SMS stores its catalogue in
  Main.mdb — a Microsoft Access / JET 4.0 file, not SQL Server as the
  project brief assumed. This reads tables, columns, primary keys, foreign
  keys and indexes via the ACE OLE DB provider.

  Always point this at a COPY of the database, never the live file.

.PARAMETER MdbPath
  Path to the .mdb to read.

.PARAMETER OutDir
  Directory to write tables.csv / columns.csv / foreignkeys.csv / indexes.csv.
#>
param(
    [Parameter(Mandatory = $true)][string]$MdbPath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Password = '',
    [switch]$IncludeRowCounts
)

Add-Type -AssemblyName System.Data

if (-not (Test-Path $MdbPath)) { throw "Not found: $MdbPath" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Prefer the newest ACE provider present.
$provider = $null
foreach ($p in @('Microsoft.ACE.OLEDB.16.0', 'Microsoft.ACE.OLEDB.12.0')) {
    if (Test-Path "HKLM:\SOFTWARE\Classes\$p") { $provider = $p; break }
}
if (-not $provider) { throw 'No ACE OLE DB provider registered.' }
Write-Host "Provider: $provider"

$sb = New-Object System.Data.OleDb.OleDbConnectionStringBuilder
$sb['Provider'] = $provider
$sb['Data Source'] = $MdbPath
$sb['Mode'] = 'Read'
if ($Password) { $sb['Jet OLEDB:Database Password'] = $Password }
$conn = New-Object System.Data.OleDb.OleDbConnection $sb.ConnectionString
$conn.Open()

try {
    # --- Tables -----------------------------------------------------------
    $tables = $conn.GetOleDbSchemaTable([System.Data.OleDb.OleDbSchemaGuid]::Tables, $null)
    $userTables = $tables | Where-Object { $_.TABLE_TYPE -eq 'TABLE' } | Sort-Object TABLE_NAME

    $tableRows = foreach ($t in $userTables) {
        $count = $null
        if ($IncludeRowCounts) {
            try {
                $cmd = $conn.CreateCommand()
                $cmd.CommandText = "SELECT COUNT(*) FROM [$($t.TABLE_NAME)]"
                $count = $cmd.ExecuteScalar()
            } catch { $count = -1 }
        }
        [pscustomobject]@{
            TableName = $t.TABLE_NAME
            TableType = $t.TABLE_TYPE
            RowCount  = $count
        }
    }
    $tableRows | Export-Csv (Join-Path $OutDir 'tables.csv') -NoTypeInformation -Encoding utf8
    Write-Host ("Tables: {0}" -f $tableRows.Count)

    # --- Columns ----------------------------------------------------------
    $cols = $conn.GetOleDbSchemaTable([System.Data.OleDb.OleDbSchemaGuid]::Columns, $null)
    $colRows = $cols |
        Where-Object { $userTables.TABLE_NAME -contains $_.TABLE_NAME } |
        Sort-Object TABLE_NAME, ORDINAL_POSITION |
        ForEach-Object {
            $t = try { [System.Data.OleDb.OleDbType]$_.DATA_TYPE } catch { $_.DATA_TYPE }
            [pscustomobject]@{
                TableName  = $_.TABLE_NAME
                Ordinal    = $_.ORDINAL_POSITION
                ColumnName = $_.COLUMN_NAME
                DataType   = $t
                MaxLength  = $_.CHARACTER_MAXIMUM_LENGTH
                Precision  = $_.NUMERIC_PRECISION
                Scale      = $_.NUMERIC_SCALE
                Nullable   = $_.IS_NULLABLE
                Default    = $_.COLUMN_DEFAULT
            }
        }
    $colRows | Export-Csv (Join-Path $OutDir 'columns.csv') -NoTypeInformation -Encoding utf8
    Write-Host ("Columns: {0}" -f $colRows.Count)

    # --- Primary keys -----------------------------------------------------
    try {
        $pks = $conn.GetOleDbSchemaTable([System.Data.OleDb.OleDbSchemaGuid]::Primary_Keys, $null)
        $pks | Select-Object TABLE_NAME, COLUMN_NAME, ORDINAL, PK_NAME |
            Sort-Object TABLE_NAME, ORDINAL |
            Export-Csv (Join-Path $OutDir 'primarykeys.csv') -NoTypeInformation -Encoding utf8
        Write-Host ("Primary keys: {0}" -f @($pks).Count)
    } catch { Write-Warning "Primary keys unavailable: $($_.Exception.Message)" }

    # --- Foreign keys (the relationship map) ------------------------------
    try {
        $fks = $conn.GetOleDbSchemaTable([System.Data.OleDb.OleDbSchemaGuid]::Foreign_Keys, $null)
        $fks | Select-Object PK_TABLE_NAME, PK_COLUMN_NAME, FK_TABLE_NAME, FK_COLUMN_NAME,
                             UPDATE_RULE, DELETE_RULE, FK_NAME |
            Sort-Object FK_TABLE_NAME, FK_NAME |
            Export-Csv (Join-Path $OutDir 'foreignkeys.csv') -NoTypeInformation -Encoding utf8
        Write-Host ("Foreign keys: {0}" -f @($fks).Count)
    } catch { Write-Warning "Foreign keys unavailable: $($_.Exception.Message)" }

    # --- Indexes ----------------------------------------------------------
    try {
        $idx = $conn.GetOleDbSchemaTable([System.Data.OleDb.OleDbSchemaGuid]::Indexes, $null)
        $idx | Select-Object TABLE_NAME, INDEX_NAME, COLUMN_NAME, ORDINAL_POSITION,
                             PRIMARY_KEY, UNIQUE, NULLS |
            Sort-Object TABLE_NAME, INDEX_NAME, ORDINAL_POSITION |
            Export-Csv (Join-Path $OutDir 'indexes.csv') -NoTypeInformation -Encoding utf8
        Write-Host ("Index entries: {0}" -f @($idx).Count)
    } catch { Write-Warning "Indexes unavailable: $($_.Exception.Message)" }
}
finally {
    $conn.Close()
}

Write-Host "Wrote schema to $OutDir"
