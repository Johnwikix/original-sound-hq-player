# One-shot script: normalize all source XAML files.
# 1) Ensure UTF-8 BOM at file head (add if missing).
# 2) Normalize line endings to CRLF (convert lone LF -> CRLF).
# 3) Repair the 12 garbled comments in View/SettingsPage.xaml
#    (line-precise replacement driven by an external UTF-8 BOM JSON).
#
# Operates on source tree only, skips bin/ and obj/.

$ErrorActionPreference = "Stop"
$root = "D:\code\winui\music_player"
$dataFile = Join-Path $root "_tools\xaml-comment-fixes.json"

# Read fix data using UTF-8 with BOM so the JSON parses correctly.
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$fixData = Get-Content -LiteralPath $dataFile -Raw -Encoding UTF8 | ConvertFrom-Json

$files = Get-ChildItem -Path $root -Recurse -Filter "*.xaml" |
    Where-Object { $_.FullName -notmatch "[\\/]bin[\\/]" -and $_.FullName -notmatch "[\\/]obj[\\/]" }

$summary = New-Object System.Collections.Generic.List[string]
$totalChanged = 0

foreach ($f in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)

    # Step 1: ensure UTF-8 BOM.
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if (-not $hasBom) {
        $newBytes = New-Object 'System.Collections.Generic.List[byte]'
        $newBytes.Add(0xEF) | Out-Null
        $newBytes.Add(0xBB) | Out-Null
        $newBytes.Add(0xBF) | Out-Null
        $bytes[0..($bytes.Length - 1)] | ForEach-Object { $newBytes.Add($_) } | Out-Null
        $bytes = $newBytes.ToArray()
        $totalChanged++
    }

    # Step 2: CRLF normalization (convert lone LF -> CRLF).
    $normalized = New-Object 'System.Collections.Generic.List[byte]'
    $changedLE = $false
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $b = $bytes[$i]
        if ($b -eq 0x0A) {
            $prev = if ($normalized.Count -gt 0) { $normalized[$normalized.Count - 1] } else { -1 }
            if ($prev -eq 0x0D) {
                $normalized.Add($b) | Out-Null
            } else {
                $normalized.Add(0x0D) | Out-Null
                $normalized.Add($b) | Out-Null
                $changedLE = $true
            }
        } else {
            $normalized.Add($b) | Out-Null
        }
    }
    if ($changedLE) { $totalChanged++ }
    $bytes = $normalized.ToArray()

    [System.IO.File]::WriteAllBytes($f.FullName, $bytes)
}

# Step 3: fix SettingsPage.xaml comments via JSON map.
foreach ($prop in $fixData.PSObject.Properties) {
    $relPath = $prop.Name
    $fullPath = Join-Path $root $relPath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        Write-Warning "Missing file: $fullPath"
        continue
    }
    $content = [System.IO.File]::ReadAllText($fullPath, $utf8Bom)
    $lines = $content -split "`r?`n"
    $fileChanged = $false

    foreach ($entry in $prop.Value.PSObject.Properties) {
        $lineNum = [int]$entry.Name
        $newText = [string]$entry.Value
        $idx = $lineNum - 1
        if ($idx -lt 0 -or $idx -ge $lines.Length) {
            Write-Warning "Line $lineNum out of range in $relPath"
            continue
        }
        $origLine = $lines[$idx]
        if ($origLine -match '^(\s*)<!--.*-->\s*$') {
            $indent = $matches[1]
            $newLine = "$indent<!--  $newText  -->"
            if ($newLine -ne $origLine) {
                $lines[$idx] = $newLine
                $fileChanged = $true
            }
        } else {
            Write-Warning "Line $lineNum in $relPath not a comment: $origLine"
        }
    }

    if ($fileChanged) {
        $newContent = ($lines -join "`r`n")
        [System.IO.File]::WriteAllText($fullPath, $newContent, $utf8Bom)
        $totalChanged++
        $summary.Add(("[FIXED] $relPath : comments repaired") ) | Out-Null
    }
}

Write-Host ("Done. Total files touched: $totalChanged")
$summary | ForEach-Object { Write-Host $_ }