# 2026-11-11 — The dungeon trail stays on its floor

Silentkelpie: "on the dungeon map the breadcumb on the map isn't tracking the z coords elevation level changes
and overlaps the breadcumbs should only show on the level of the map that is on and not overlap to a level
above or below unless that level is active on the map".

## The dungeon map's walked cells (RynthAi radar snapshot + RynthCore's Dungeon Map)
- RynthAi recorded where you walked by x/y only. Walking one floor therefore marked the same spot on every
  floor above and below it, so going up or down showed a "trail" where you had never been.
- Each floor now has its own walked cells. The paint goes to the floor under you: of the floors that have
  floor at your spot, the one nearest your height; failing that, the nearest by height. It is laid only
  where that floor has floor.
- The Dungeon Map draws each shown floor's own walked cells: your floor in full, the other shown floors
  dimmed like their walls. A floor that isn't shown draws none. (Before, only your floor's cells were drawn,
  but they included the other floors' paint.)

## The 3D breadcrumb trail (Nav panel's Breadcrumbs)
- Indoors, only trail segments within half a storey (3 units) of your height are drawn. The overlay isn't
  hidden by floors, so another floor's trail drew straight over this one.
- Height now counts when sampling: a rise or drop of 1.5 units samples a crumb even within the spacing, so
  the trail follows ramps and stairs.
- A drop or lift of more than 3 units between two ticks (a pit, a jump down) breaks the trail there, so no
  line runs from one floor to the next.
