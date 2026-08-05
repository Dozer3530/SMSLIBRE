<#
.SYNOPSIS
  Tries printable strings embedded in native DLLs as the JET database password,
  verifying each against the ACE OLE DB provider.

.DESCRIPTION
  Stage 2 helper. SMS builds "Jet OLEDB:Database Password=%s" in native code
  (ALD_Data.dll). If the password is a hardcoded literal it appears in that
  DLL's string table. This harvests candidate strings, filters to password-like
  tokens, and tests them against the user's own database.

  Uses OleDbConnectionStringBuilder so candidates containing ';' or '"' cannot
  corrupt the connection string.
#>
param(
    [Parameter(Mandatory = $true)][string]$MdbPath,
    [Parameter(Mandatory = $true)][string[]]$DllPath,
    [int]$MinLen = 5,
    [int]$MaxLen = 24,
    [switch]$WideOnly,
    [switch]$LatencyTest
)

Add-Type -AssemblyName System.Data

[string]$provider = @('Microsoft.ACE.OLEDB.16.0', 'Microsoft.ACE.OLEDB.12.0') |
    Where-Object { Test-Path "HKLM:\SOFTWARE\Classes\$_" } | Select-Object -First 1
if (-not $provider) { throw 'No ACE provider.' }

function Test-Password([string]$pwd) {
    $sb = New-Object System.Data.OleDb.OleDbConnectionStringBuilder
    $sb['Provider'] = $provider
    $sb['Data Source'] = $MdbPath
    $sb['Mode'] = 'Read'
    $sb['Jet OLEDB:Database Password'] = $pwd
    $c = New-Object System.Data.OleDb.OleDbConnection $sb.ConnectionString
    try { $c.Open(); $c.Close(); return $true } catch { return $false } finally { $c.Dispose() }
}

if ($LatencyTest) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    1..10 | ForEach-Object { [void](Test-Password "wrongpw$_") }
    $sw.Stop()
    Write-Host ("Avg failed-open latency: {0:N1} ms" -f ($sw.Elapsed.TotalMilliseconds / 10))
    return
}

# Harvest unique candidate strings.
$set = [System.Collections.Generic.HashSet[string]]::new()
$rx = "[\x21-\x7E]{$MinLen,$MaxLen}"                       # printable, no spaces
$encodings = if ($WideOnly) { @([System.Text.Encoding]::Unicode) }
             else { @([System.Text.Encoding]::ASCII, [System.Text.Encoding]::Unicode) }
foreach ($p in $DllPath) {
    if (-not (Test-Path $p)) { Write-Warning "missing $p"; continue }
    $bytes = [System.IO.File]::ReadAllBytes($p)
    foreach ($enc in $encodings) {
        $txt = $enc.GetString($bytes)
        foreach ($m in [regex]::Matches($txt, $rx)) { [void]$set.Add($m.Value) }
    }
}

# Keep only password-plausible tokens: drop obvious code/paths/identifiers.
$candidates = $set | Where-Object {
    $_ -notmatch '[\\/]' -and                # no path separators
    $_ -notmatch '\.(cs|dll|exe|mdb|h|cpp)$' -and
    $_ -notmatch '(::|->|@@|\$\$)' -and       # no C++ mangling
    $_ -notmatch '^\d+$'                      # not pure numbers
}
Write-Host ("Harvested {0} strings; {1} pass the password-like filter" -f $set.Count, @($candidates).Count)

if (Test-Password '') { Write-Host "Password is BLANK"; return }

$i = 0
foreach ($cand in $candidates) {
    $i++
    if ($i % 1000 -eq 0) { Write-Host ("  ...tested {0}" -f $i) }
    if (Test-Password $cand) {
        Write-Host ""
        Write-Host "RECOVERED PASSWORD: '$cand'" -ForegroundColor Green
        Set-Content -Path (Join-Path (Split-Path $MdbPath) 'recovered-password.txt') -Value $cand -NoNewline
        return
    }
}
Write-Host "No embedded string opened the database." -ForegroundColor Yellow
