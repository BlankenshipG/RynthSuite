<#
  Make-GearNames.ps1 - rebuilds the Gear tab's name tables in RynthOracle's Resources from ACE's
  enums (ACE.Entity\Enum):
    spellcategories.csv  SpellCategory  (portal.dat SpellBase.Category)
    equipmentsets.csv    EquipmentSet   (PropertyInt.EquipmentSetId 265)
    materials.csv        MaterialType   (PropertyInt.MaterialType 131, the Mag-style line's first word)
    masteries.csv        WeaponType     (PropertyInt.WeaponType 353, "(Sword)")
    skills.csv           Skill          (wield / activation requirements, "Melee Defense 300")
  The numbers are the client's own; servers that add entries keep the retail numbers, so one table
  serves every server. Names stay as ACE spells them (CamelCase); the tab adds the spaces.

  Run:  .\Make-GearNames.ps1                       (reads ..\..\..\ACECustom\Source\ACE.Entity\Enum)
        .\Make-GearNames.ps1 -EnumDir path\to\Enum
#>
param(
    [string]$EnumDir = (Join-Path $PSScriptRoot "..\..\..\ACECustom\Source\ACE.Entity\Enum"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\Plugins\RynthCore.Plugin.RynthOracle\Resources")
)
$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# The enum's members as "number,Name" rows. Explicit "Name = 12" values are kept; a member with no
# value is the previous one plus one (C#'s rule), so Skill and WeaponType need no numbers. Comments,
# attributes and anything after the enum's closing brace are ignored. First name wins for a number.
function Read-Enum([string]$file, [string]$enumName) {
    $seen = @{}
    $rows = New-Object System.Collections.Generic.List[string]
    $inEnum = $false
    $next = 0
    foreach ($raw in [System.IO.File]::ReadAllLines($file)) {
        $line = ($raw -replace '//.*$', '' -replace '/\*.*?\*/', '').Trim()
        if (-not $inEnum) {
            if ($line -match "\benum\s+$enumName\b") { $inEnum = $true }
            continue
        }
        if ($line.StartsWith('}')) { break }
        if ($line -match '^\[') { continue }
        if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*,?$') {
            $n = if ($Matches[2].StartsWith('0x')) { [Convert]::ToInt32($Matches[2].Substring(2), 16) } else { [int]$Matches[2] }
            $name = $Matches[1]
        }
        elseif ($line -match '^([A-Za-z_][A-Za-z0-9_]*)\s*,?$') {
            $n = $next
            $name = $Matches[1]
        }
        else { continue }
        $next = $n + 1
        if (-not $seen.ContainsKey($n)) { $seen[$n] = $true; $rows.Add("$n,$name") }
    }
    return $rows
}

$tables = @(
    @("SpellCategory", "spellcategories.csv"),
    @("EquipmentSet", "equipmentsets.csv"),
    @("MaterialType", "materials.csv"),
    @("WeaponType", "masteries.csv"),
    @("Skill", "skills.csv")
)
foreach ($t in $tables) {
    $rows = Read-Enum (Join-Path $EnumDir "$($t[0]).cs") $t[0]
    $out = Join-Path $OutDir $t[1]
    [System.IO.File]::WriteAllText($out, "Id,Name`n" + ($rows -join "`n") + "`n", $utf8)
    Write-Host "$($t[1]): $($rows.Count) names"
}
