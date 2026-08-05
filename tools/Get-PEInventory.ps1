<#
.SYNOPSIS
  Walks a directory tree and classifies every PE file (.exe/.dll) as managed
  (.NET) or native, recording architecture and version metadata.

.DESCRIPTION
  Stage 1 of the SMSLIBRE port project. Reads only the PE header of each file
  (no loading, no execution) and emits a CSV inventory.

  Managed detection: data directory entry 14 (CLI header) has a non-zero RVA.
#>
param(
    [string]$Root   = 'C:\Program Files\Ag Leader Technology',
    [string]$OutCsv = 'C:\Users\zkomarnisky\GIT\SMSLIBRE\analysis\inventory\pe-inventory.csv'
)

function Get-PEInfo {
    param([string]$Path)

    $result = [ordered]@{
        Machine = 'unknown'; PEFormat = ''; Managed = $false; Error = ''
    }
    try {
        $fs  = [System.IO.File]::OpenRead($Path)
        $buf = New-Object byte[] 4096
        $n   = $fs.Read($buf, 0, 4096)
        $fs.Close()

        if ($n -lt 512 -or $buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) {   # 'MZ'
            $result.Error = 'not a PE file'; return [pscustomobject]$result
        }

        $peOff = [BitConverter]::ToInt32($buf, 0x3C)
        if ($peOff -le 0 -or ($peOff + 120) -ge $n) {
            $result.Error = 'PE header beyond probe window'; return [pscustomobject]$result
        }
        if ([BitConverter]::ToUInt32($buf, $peOff) -ne 0x00004550) {   # 'PE\0\0'
            $result.Error = 'bad PE signature'; return [pscustomobject]$result
        }

        $machine = [BitConverter]::ToUInt16($buf, $peOff + 4)
        $result.Machine = switch ($machine) {
            0x014C  { 'x86' }
            0x8664  { 'x64' }
            0x01C4  { 'ARM' }
            0xAA64  { 'ARM64' }
            default { ('0x{0:X4}' -f $machine) }
        }

        # Optional header magic decides where the data directories start.
        $optOff = $peOff + 24
        $magic  = [BitConverter]::ToUInt16($buf, $optOff)
        if ($magic -eq 0x20B) {
            $result.PEFormat = 'PE32+'; $dirBase = $optOff + 112
        } elseif ($magic -eq 0x10B) {
            $result.PEFormat = 'PE32';  $dirBase = $optOff + 96
        } else {
            $result.Error = ('unknown optional header magic 0x{0:X}' -f $magic)
            return [pscustomobject]$result
        }

        # Data directory index 14 == CLI/COR20 header. Non-zero RVA => managed.
        $clrOff = $dirBase + (14 * 8)
        if (($clrOff + 8) -lt $n) {
            $result.Managed = ([BitConverter]::ToUInt32($buf, $clrOff) -ne 0)
        }
    }
    catch { $result.Error = $_.Exception.Message }

    [pscustomobject]$result
}

Write-Host "Scanning $Root ..."
$files = Get-ChildItem -Path $Root -Recurse -File -Include *.exe, *.dll -ErrorAction SilentlyContinue
Write-Host ("Found {0} PE candidates" -f $files.Count)

$rows = foreach ($f in $files) {
    $pe = Get-PEInfo -Path $f.FullName
    $vi = $f.VersionInfo

    [pscustomobject][ordered]@{
        RelPath     = $f.FullName.Substring($Root.Length).TrimStart('\')
        Name        = $f.Name
        Ext         = $f.Extension.ToLower()
        SizeKB      = [math]::Round($f.Length / 1KB, 1)
        Kind        = if ($pe.Error) { 'ERROR' } elseif ($pe.Managed) { 'managed' } else { 'native' }
        Machine     = $pe.Machine
        PEFormat    = $pe.PEFormat
        Company     = ($vi.CompanyName     -replace '\s+', ' ').Trim()
        Product     = ($vi.ProductName     -replace '\s+', ' ').Trim()
        FileVersion = $vi.FileVersion
        Description = ($vi.FileDescription -replace '\s+', ' ').Trim()
        Error       = $pe.Error
    }
}

$dir = Split-Path $OutCsv -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$rows | Sort-Object Kind, RelPath | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding utf8

Write-Host ""
Write-Host "=== Summary by kind ==="
$rows | Group-Object Kind | Sort-Object Count -Descending |
    Select-Object Count, Name | Format-Table -AutoSize

Write-Host "=== Summary by architecture ==="
$rows | Group-Object Machine | Sort-Object Count -Descending |
    Select-Object Count, Name | Format-Table -AutoSize

Write-Host "Wrote $OutCsv"
