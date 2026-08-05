<#
.SYNOPSIS
  Extracts printable ASCII and UTF-16LE strings from binaries and optionally
  filters them by regex.

.DESCRIPTION
  Used in Stages 1-3 to recover connection strings, registry paths, file-format
  magic values and Win32 API usage from the native (C++) parts of SMS that no
  IL decompiler can reach.

.EXAMPLE
  .\Find-BinaryStrings.ps1 -Path 'C:\...\ALMappingLib.dll' -Pattern 'Provider='
#>
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string]$Pattern,
    [int]$MinLength = 5,
    [int]$Context = 0
)

foreach ($p in $Path) {
    if (-not (Test-Path $p)) { Write-Warning "missing: $p"; continue }
    $bytes = [System.IO.File]::ReadAllBytes($p)

    # Latin1 maps bytes 1:1 to chars, so offsets stay meaningful.
    $ascii = [System.Text.Encoding]::Latin1.GetString($bytes)
    $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)

    $rx = "[\x20-\x7E]{$MinLength,}"
    foreach ($set in @(@{N = 'ascii'; S = $ascii }, @{N = 'utf16'; S = $utf16 })) {
        foreach ($m in [regex]::Matches($set.S, $rx)) {
            $s = $m.Value
            if ($Pattern -and $s -notmatch $Pattern) { continue }
            [pscustomobject]@{
                File     = Split-Path $p -Leaf
                Encoding = $set.N
                Offset   = if ($set.N -eq 'ascii') { $m.Index } else { $m.Index * 2 }
                Text     = $s
            }
        }
    }
}
