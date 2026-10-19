<#
  Make-GearNames.ps1 - rebuilds RynthOracle's Resources\spellcategories.csv and
  Resources\equipmentsets.csv (the Gear tab's spell family and equipment set names) from ACE's
  enums: ACE.Entity\Enum\SpellCategory.cs and EquipmentSet.cs. The numbers are the client's own
  (portal.dat SpellBase.Category, PropertyInt.EquipmentSetId 265); servers that add families or
  sets keep the retail numbers, so one table serves every server. Names stay as ACE spells them
  (CamelCase); the tab adds the spaces.

  Run:  .\Make-GearNames.ps1                       (reads ..\..\..\ACECustom\Source\ACE.Entity\Enum)
        .\Make-GearNames.ps1 -EnumDir path\to\Enum
#>
param(
    [string]$EnumDir = (Join-Path $PSScriptRoot "..\..\..\ACECustom\Source\ACE.Entity\Enum"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\Plugins\RynthCore.Plugin.RynthOracle\Resources")
)
$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# "Name = 123," lines of one enum, as "123,Name" rows (first name wins for a repeated number).
function Read-Enum([string]$file) {
    $seen = @{}
    $rows = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadAllLines($file)) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(\d+)\s*,?') {
            $n = [int]$Matches[2]
            if (-not $seen.ContainsKey($n)) { $seen[$n] = $true; $rows.Add("$n,$($Matches[1])") }
        }
    }
    return $rows
}

foreach ($pair in @(@("SpellCategory.cs", "spellcategories.csv"), @("EquipmentSet.cs", "equipmentsets.csv"))) {
    $rows = Read-Enum (Join-Path $EnumDir $pair[0])
    $out = Join-Path $OutDir $pair[1]
    [System.IO.File]::WriteAllText($out, "Id,Name`n" + ($rows -join "`n") + "`n", $utf8)
    Write-Host "$($pair[1]): $($rows.Count) names"
}
