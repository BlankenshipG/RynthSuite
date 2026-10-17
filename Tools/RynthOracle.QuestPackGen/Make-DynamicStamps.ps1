<#
  Make-DynamicStamps.ps1 - rebuilds RynthOracle's Resources\dynstamps.csv (the Dynamic Stamps tab,
  Infinite Leaftide) from the players' "ExtraDyn" Google sheet (one sheet per area; the
  leaftidewiki Dynamic Stamps page needs a login, the sheet doesn't).

  Each area sheet: "Town NPCs, Stamp, Reward, Korwith, Korwith II, Notes". A row with only the
  first column is a sub-area heading (Frost Haven, Dark Isle). The first column of a stamp row is
  the place when the reward reads "X gives you Y" (X is the NPC), else it is the NPC. Rows without
  a Dynamic_ stamp (NO STAMP, duplicates) are skipped; a stamp is kept once.

  Run:  .\Make-DynamicStamps.ps1            (writes ..\..\Plugins\RynthCore.Plugin.RynthOracle\Resources\dynstamps.csv)
        .\Make-DynamicStamps.ps1 -Xlsx path\to\ExtraDyn.xlsx   (use a downloaded copy)
#>
param(
    [string]$SheetId = "1SyG2GDjZ0CoS2URFPMcUnI8R0geh03KkYzRYzsPhJGg",
    [string]$Xlsx = "",
    [string]$Dst = (Join-Path $PSScriptRoot "..\..\Plugins\RynthCore.Plugin.RynthOracle\Resources\dynstamps.csv")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Xlsx) {
    $Xlsx = Join-Path $env:TEMP "ExtraDyn.xlsx"
    Invoke-WebRequest -UseBasicParsing "https://docs.google.com/spreadsheets/d/$SheetId/export?format=xlsx" -OutFile $Xlsx -TimeoutSec 120
}

# Areas in list order (the workbook's tab order, Odd Stamps after Looking Around).
$order = "Facility Hub","Looking Around","Odd Stamps","Odd Stamps 2","Bandit Castle","Bur","Gear Knight","Wai Jhou","Rynthid",
         "Paradox Camp","Graveyard","Neftet","Frozen Valley","Hoshino Tent","Viridian Rise","Ayan","Candeth Keep"

# ── Read every sheet's rows (cells by column) out of the .xlsx ──────────────────
$zip = [System.IO.Compression.ZipFile]::OpenRead($Xlsx)
function ReadXml($name) {
    $e = $zip.GetEntry($name); if (-not $e) { return $null }
    $sr = New-Object IO.StreamReader($e.Open()); $t = $sr.ReadToEnd(); $sr.Close(); [xml]$t
}
$strings = @()
$ss = ReadXml "xl/sharedStrings.xml"
if ($ss) { foreach ($si in $ss.sst.si) { $strings += $si.InnerText } }
$wb = ReadXml "xl/workbook.xml"
$rels = ReadXml "xl/_rels/workbook.xml.rels"
$relMap = @{}; foreach ($r in $rels.Relationships.Relationship) { $relMap[$r.Id] = $r.Target }
$sheets = @{}
foreach ($s in $wb.workbook.sheets.sheet) {
    $rid = $s.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
    $sx = ReadXml ("xl/" + ($relMap[$rid] -replace '^/xl/', ''))
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($row in $sx.worksheet.sheetData.row) {
        $cells = @{}
        foreach ($c in $row.c) {
            $idx = 0; foreach ($ch in (($c.r -replace '\d', '')).ToCharArray()) { $idx = $idx * 26 + ([int]$ch - 64) }
            $v = if ($c.t -eq "s") { $strings[[int]$c.v] } elseif ($c.t -eq "inlineStr") { $c.is.InnerText } else { $c.v }
            if ($v) { $cells[$idx] = ($v -replace "`r?`n", " / ") }
        }
        if ($cells.Count -gt 0) { $rows.Add($cells) }
    }
    $sheets[$s.name] = $rows
}
$zip.Dispose()

# ── Build the stamp rows ────────────────────────────────────────────────────────
$enDash = [string][char]0x2013; $emDash = [string][char]0x2014; $arrow = [string][char]0x2192
function Clean([string]$s) { if (-not $s) { return "" }; ($s.Replace($enDash, "-").Replace($emDash, "-").Replace($arrow, "->")).Trim() }
function Q([string]$s) { '"' + ($s -replace '"', '""') + '"' }
$coordRx = '\d{1,3}(?:\.\d)?\s*[NSns][,\s]+\d{1,3}(?:\.\d)?\s*[EWew]'

$out = New-Object System.Collections.Generic.List[string]
$out.Add("Area,Place,Npc,Reward,Stamp,Note")
$seen = @{}; $skipped = 0
foreach ($area in $order) {
    if (-not $sheets.ContainsKey($area)) { Write-Warning "sheet '$area' not in the workbook"; continue }
    $place = ""
    foreach ($cells in ($sheets[$area] | Select-Object -Skip 1)) {   # row 1 is the header
        $a = ((Clean $cells[1]) -replace '\s*-+\s*$', '').Trim()
        $stamp = Clean $cells[2]; $rew = Clean $cells[3]; $note = Clean $cells[6]
        if ($a -and -not $stamp -and -not $rew) { $place = $a; continue }
        if ($stamp -notmatch '(?i)^dynamic_\d+_\S+') { $skipped++; continue }
        $key = $stamp.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = 1
        $npc = ""; $reward = $rew
        if ($rew -match '^(?<n>.+?) gives you (?<r>.+?)\.?\s*$') { $npc = $Matches.n; $reward = $Matches.r }
        elseif ($rew -match '^You allow (?<n>.+?) to examine your (?<r>.+?)\.?\s*$') { $npc = $Matches.n; $reward = "examines your " + $Matches.r }
        $rowPlace = $place
        if ($a) { if ($npc) { $rowPlace = $a } else { $npc = $a } }
        $cm = [regex]::Match($rowPlace, $coordRx)
        if ($cm.Success -and $note.IndexOf($cm.Value) -lt 0) { $note = ($cm.Value + " " + $note).Trim() }
        $out.Add(((Q $area), (Q $rowPlace), (Q $npc), (Q $reward.TrimEnd('.').Trim()), (Q $stamp), (Q $note)) -join ",")
    }
}
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($Dst), $out, (New-Object Text.UTF8Encoding($false)))
"dynstamps.csv: $($out.Count - 1) stamps ($skipped rows without a stamp skipped) -> $([IO.Path]::GetFullPath($Dst))"
