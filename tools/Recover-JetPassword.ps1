<#
.SYNOPSIS
  Recovers the database password of a JET4 (.mdb) file from its header, and
  verifies it by opening the database with the ACE OLE DB provider.

.DESCRIPTION
  Stage 2 helper. SMS supplies "Jet OLEDB:Database Password" at runtime, so the
  schema cannot be read without it. For JET4 the password is stored at header
  offset 0x42, XOR-masked with a key derived from the creation-date double at
  offset 0x72. This is the user's own database on the user's own machine.

  Every candidate is validated against ACE before being reported, so a wrong
  decode can never be mistaken for the real password.
#>
param(
    [Parameter(Mandatory = $true)][string]$MdbPath
)

Add-Type -AssemblyName System.Data

$b = New-Object byte[] 0x100
$fs = [System.IO.File]::OpenRead($MdbPath); $null = $fs.Read($b, 0, 0x100); $fs.Close()

$enc = $b[0x42..(0x42 + 0x27)]                 # 40 encoded bytes
$dateVal = [BitConverter]::ToDouble($b, 0x72)  # OLE automation date
$dateInt = [int][math]::Truncate($dateVal)
$mask4 = [BitConverter]::GetBytes($dateInt)    # little-endian int32

Write-Host ("creation-date double : {0}" -f $dateVal)
Write-Host ("date as int32        : {0}  mask bytes = {1}" -f $dateInt, (($mask4 | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))

function Test-Password([string]$pwd) {
    foreach ($prov in @('Microsoft.ACE.OLEDB.16.0', 'Microsoft.ACE.OLEDB.12.0')) {
        if (-not (Test-Path "HKLM:\SOFTWARE\Classes\$prov")) { continue }
        $cs = "Provider=$prov;Data Source=$MdbPath;Mode=Read;Jet OLEDB:Database Password=$pwd;"
        $c = New-Object System.Data.OleDb.OleDbConnection $cs
        try { $c.Open(); $c.Close(); return $true } catch { } finally { $c.Dispose() }
    }
    return $false
}

# Candidate decodes. JET4 masks with the repeating 4-byte date key; some writers
# also salt every other byte with 0x02 for the ACE "new format" flag.
$candidates = @{}

$decoded = for ($i = 0; $i -lt $enc.Length; $i++) { $enc[$i] -bxor $mask4[$i % 4] }
$candidates['date-xor-utf16'] = ([System.Text.Encoding]::Unicode.GetString([byte[]]$decoded))

$decoded2 = for ($i = 0; $i -lt $enc.Length; $i++) {
    $v = $enc[$i] -bxor $mask4[$i % 4]
    if ($i % 2 -eq 1) { $v = $v -bxor 0x02 }   # de-salt the high byte of each UTF-16 unit
    $v
}
$candidates['date-xor-desalt-utf16'] = ([System.Text.Encoding]::Unicode.GetString([byte[]]$decoded2))

# Also try treating the decoded stream as ASCII (some passwords are stored 1-byte).
$candidates['date-xor-ascii'] = ([System.Text.Encoding]::ASCII.GetString([byte[]]$decoded))

$found = $null
foreach ($k in $candidates.Keys) {
    $raw = $candidates[$k]
    # Trim at first NUL, keep printable prefix.
    $nul = $raw.IndexOf([char]0)
    $s = if ($nul -ge 0) { $raw.Substring(0, $nul) } else { $raw }
    $printable = -join ($s.ToCharArray() | Where-Object { [int]$_ -ge 32 -and [int]$_ -lt 127 })
    Write-Host ("[{0,-24}] '{1}'" -f $k, $printable)

    foreach ($cand in @($printable, $s)) {
        if ([string]::IsNullOrEmpty($cand)) { continue }
        if (Test-Password $cand) { $found = $cand; break }
    }
    if ($found) { break }
}

# Blank password, just in case the header bytes are stale.
if (-not $found -and (Test-Password '')) { $found = '(blank)' }

Write-Host ''
if ($found) {
    Write-Host "RECOVERED PASSWORD: '$found'" -ForegroundColor Green
    Set-Content -Path (Join-Path (Split-Path $MdbPath) 'recovered-password.txt') -Value $found -NoNewline
} else {
    Write-Host "No candidate opened the database. Header decode may differ for this writer." -ForegroundColor Yellow
}
