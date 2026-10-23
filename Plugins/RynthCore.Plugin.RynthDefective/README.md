# RynthDefective

An item bar for RynthCore with one-touch use, and a fast weapon picker. Version 0.1.0.

Credit: RynthDefective is an independent implementation of the ideas of **Defective**, the Decal
item-bar plugin written by Hazridi and modified by Aun (<https://github.com/aunrela/Defective>). It
contains no code from it: it was written from the plugin's public feature description
against RynthCore's own APIs. (Defective's repository states no licence; ask Aun if you want a
literal port or shared code.)

## What it does

**Slots.** The bar is a row (or grid) of slots, each holding one item: a healing kit, a stamina kit,
a potion, a scroll, a weapon, a wand, a shield... Click a slot, or press its key, and the item is used
**without selecting anything in the game**:

| Item | What a press does |
| --- | --- |
| Healing / stamina kit | used on yourself |
| Potion, scroll, food, gem, anything else | used (a double-click) |
| Weapon, wand, shield, armor, jewelry | wielded / worn (a double-click); an item already in hand is left alone |

A slot can be set to another way: use it, use it on me, use it on the selected target, or wield it.

**Put an item in a slot** (Edit slots in the bar): *Set from selected* takes the item you have selected
in the game, or *Pick from pack* chooses from a searchable list of what you carry. `/rdef set 3` does the
first, `/rdef set 3 Health Potion` the second.

**A slot keeps the item's identity, not just the object.** It remembers the item's name, wcid and
material as well as its object id. When the exact object is gone (a used-up stack, a replaced weapon)
the slot uses another carried item of the same identity: not in hand before in hand, the smaller stack
first. A slot with nothing to use shows the last picture seen, dimmed, and "none"; it fills itself again
when you carry another.

**Counts.** Under each slot: the uses left on all the kits of that kind (28), the stack count (x120), "in
hand" for the weapon you hold, "none" when none is carried, and the slot's key.

**Keys.** Keys are the engine's hotkeys running `/rdef use <slot>`. Type a key in the slot editor (F9,
Ctrl+1, Alt+Shift+Num3) and press *Bind to this slot* (follows the slot) or *Bind to this item* (runs
`/rdef use <the item's name>`, works for every character on this PC). The same from chat: `/rdef bind 3 F9`.
Any item can also be bound by name straight from Settings > Hotkeys or `/rc hotkey add F9 /rdef use Stamina Kit`.
`/rdef use <name>` takes a full name, the start of a name or words in it ("Greater Stamina Kit",
"stamina kit"); of several matches the one not in hand, with the smaller stack, then the lower id, is used.

**The weapon picker** (the *Weapons* button, `/rdef weapons [search]`): every weapon, wand, shield and ammunition
stack you carry, as big rows with the item's picture, grouped by kind (melee, missile, war magic, void
magic, shields, ammunition), A-Z. Type words in the search box ("fire wand", "axe", "void"); the kind
buttons (All, Melee, Missile, War, Void) narrow it, and the one you leave pressed is remembered. The weapon in
hand is highlighted. **One click wields.** `+` in front of a row adds a favourite; favourites and the last
weapons you wielded are listed first (they are also in their kind's group).

## Busy, combat and RynthAi

* One item action at a time: a request waits while the client is busy, a gesture animates, RynthAi waits on
  the server, or the last action is unanswered (the server's UseDone; a wield is answered by the item
  reading in hand). Pressing again while one waits does not queue a second; pressing another slot replaces the
  waiting one. A request not sent within 6 s gives up and says why.
* After a wield from a fighting stance the stance follows the weapon (wand: magic, melee weapon: melee,
  launcher with ammunition in hand: missile) once the character is still. At peace you stay at peace. A bow
  without arrows in hand returns you to peace rather than leaving a stance it can't use. Shields, armor and
  rings leave the stance alone.
* While RynthAi's macro runs, weapons/wands/shields are **not** wielded (it picks the weapon itself and would
  swap back) unless *Allow weapon swaps while RynthAi's macro runs* is on. Kits, potions and the rest always work.

## Commands

```
/rdef                         show / hide the bar
/rdef use <slot | item name>  use the slot's item, or the carried item with that name
/rdef set <slot> [item name]  put the selected item (or the named one) in a slot
/rdef clear <slot>            empty a slot
/rdef bind <slot> <key>       bind a key (F9, Ctrl+1 ...) to the slot; /rdef unbind <slot>
/rdef weapons [search]        show the weapon picker
/rdef fav                     make the selected weapon a favourite (or take it out)
/rdef slots <n>               how many slots (1-24)
/rdef list | status | help
```

## Settings

Slots (1-24), slots per row, button size, picker row icon size, item icons on/off, counts and keys on/off,
allow weapon swaps under RynthAi. Kept per character with the slots, favourites and recent weapons in
`<RynthSuite>\RynthDefective\characters\<server>_<character>.json`.

## Not in 0.1

Pages, spell slots, arrow auto-equip, gear highlight, summon cooldown counters and the war-magic element
changer (Defective has them).

## Needs

RynthCore engine API 71 with ImGui for the windows (op level 2 for pictures; text buttons without it).
`/rdef use` works on any engine with UseObject.
