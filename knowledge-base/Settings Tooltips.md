# User-Facing Settings Tooltip Catalog

Review draft for the settings tooltips planned for AOCCH. This file lists the
visible setting label and the proposed tooltip text; it does not change the
plugin UI.

Scope:

- `ConfigWindow` settings tabs.
- `NorthHornStatusWindow` user-configurable options.
- Debug-window controls are intentionally excluded.
- Transient navigation controls, such as shopping page, tab, and item
  selectors, are not settings and are not included.

## Critical Engagements

| Setting | Proposed tooltip |
| --- | --- |
| Enable CE Farming | Automatically joins and runs Critical Engagements during farm sessions. Turn this off to skip CEs entirely. |
| Prioritize CE | Prioritizes available CEs over running FATEs or other activities. |
| Enabled Critical Engagements | Pick which CEs to join in this zone. Unchecked CEs will be ignored. |

## FATEs

| Setting | Proposed tooltip |
| --- | --- |
| Enable FATE Farming | Automatically farms FATEs during farm sessions. Turn this off if you don't want to farm FATEs. |
| FATE Priority | Decides which FATE to head to next. 'Lowest Progress' targets newly spawned FATEs, while 'Nearest' picks whichever is closest. |
| FATE Dismount Distance | How close to get to the FATE marker before dismounting (5 to 50 yalms). |
| Enabled FATEs | Pick which FATEs to farm in this zone. Unchecked FATEs will be skipped. |

## Pots

| Setting | Proposed tooltip |
| --- | --- |
| Enable Pot Farming | Enables automated pot FATE cycling and treasure hunting. |
| Starting Pot FATE | Choose which pot FATE to kick off the route with in this zone. 'Auto' picks the best starting point for you. |
| Spawn Lead Minutes | How many minutes before a pot FATE spawns to head over and wait for it. |
| Arrival Radius | How close you need to be to the pot FATE marker before stopping to wait. |
| Use Ninja For Dangerous Area | Switches to Ninja and uses Hide to sneak through dangerous high-level areas on foot. (Experimental; recommended for max Knowledge level). |
| Ninja Gearset Number | Your Ninja gearset number. Used whenever sneak travel is required. |
| FATE Gearset Number | The gearset number you want to swap to when fighting FATEs. |
| Live Knowledge Hide Offset | Adjusts the mob level threshold for using Hide relative to your Knowledge level. Set to 0 to hide from mobs at your level or higher. |
| Knowledge Threat Enter Range | Triggers Hide when a dangerous mob comes within this distance. |
| Knowledge Threat Exit Range | Distance required to clear dangerous mobs before mounting back up. Keep this higher than Enter Range. |
| Fallback Maximum Aggro Level | Mob aggro level limit used as a safety fallback when live zone knowledge data isn't loaded. |
| Fallback Hide Threshold Distance | Threat detection distance used as a fallback when live zone knowledge data isn't loaded. |
| Maximum Aggro Level | Skips pot locations if nearby mobs exceed this level (used when Ninja travel is turned off). |
| Manage Instance Time | Tracks remaining instance time so you don't start a new pot cycle if you're about to get booted from the zone. |
| FATE Completion Budget Minutes | Estimated time needed to finish a FATE. Won't start a FATE if a pot departure is coming up sooner than this. |
| Treasure Hunt Budget Minutes | Estimated time needed to complete a treasure step before the next pot departure. |
| Instance Exit Buffer Minutes | Safety margin left before the instance timer expires to safely leave or re-queue. |
| CE Fallback Cutoff Minutes | Stops joining fallback CEs if a pot departure is scheduled within this many minutes. |
| FATE Fallback Cutoff Minutes | Stops starting fallback FATEs if a pot departure is scheduled within this many minutes. |

## Treasure Coffers

| Setting | Proposed tooltip |
| --- | --- |
| Enable Automatic Coffer Route | Automatically scans for overworld coffers at base camp using Treasuresight and runs the route if enough coffers are reported. |
| Enable Overworld Treasure Guide | Draws a visual guide line and marker in-game pointing to the closest coffer. Purely visual—doesn't automate movement. |
| Automatic Silver Threshold | Minimum Silver Coffers needed from a Treasuresight scan to trigger the automatic route (0 = any). |
| Automatic Bronze Threshold | Minimum Bronze Coffers needed from a Treasuresight scan to trigger the automatic route (0 = any). |
| Arrival Distance | How close to get to a coffer spot before performing a final search and moving on. |
| Skip High-Level Caverns During Ashkin | Bypasses high-level cavern coffers when aggressive Ashkin mobs are active at night. |
| Skip Unsafe-Weather Routes | Avoids dangerous route paths when unsafe weather spawns aggressive mobs. |
| Use Ninja For Dangerous Coffers | Switches to Ninja and uses Hide to safely reach dangerous coffer spots on foot. (Experimental; recommended for max Knowledge level). |
| Ninja Gearset Number | Your Ninja gearset number. Used whenever sneak travel is required. |
| FATE Gearset Number | The gearset number you want to swap to when fighting FATEs during a coffer run. |
| Live Knowledge Hide Offset | Adjusts the mob level threshold for using Hide relative to your Knowledge level. Set to 0 to hide from mobs at your level or higher. |
| Knowledge Threat Enter Range | Triggers Hide when a dangerous mob gets within this distance. |
| Knowledge Threat Exit Range | Distance required to clear dangerous mobs before mounting back up. Keep this higher than Enter Range. |
| Fallback Maximum Aggro Level | Aggro level limit used as a safety fallback when live zone knowledge data isn't loaded. |
| Fallback Hide Threshold Distance | Threat detection distance used as a fallback when live zone knowledge data isn't loaded. |
| Maximum Aggro Level | Skips coffer spots if nearby mobs exceed this level (used when Ninja travel is turned off). |

## General Settings

| Setting | Proposed tooltip |
| --- | --- |
| Share Confirmed Coffer Observations | Anonymously sends confirmed coffer locations to help map out spawn points for the community. No character or account data is ever sent. |
| Autorotation Override Preset Name | Type a BossMod preset name to override the default rotation logic. Leave blank to let AOCCH manage your rotation automatically. |
| Melee Target Range | Max targeting range for melee jobs before engaging (1.1 to 30 yalms). |
| Ranged Target Range | Max targeting range for ranged and caster jobs before engaging (1.1 to 30 yalms). |
| Enable Buff Rotation | Automatically applies job and foray buff actions during combat and route travel. |
| Use Return | Uses the Return spell to quickly teleport back to base camp when needed. |
| Minimum Mounting Range | Only mounts up if your destination is further away than this distance. Walks instead for shorter distances. |
| Main Window Status Text Size | Adjusts the status font size in the main window (85% to 150%). |
| Show Tooltips | Shows helpful descriptions when you hover over settings and interface buttons. |

## Shopping

| Setting | Proposed tooltip |
| --- | --- |
| Enable Shopping | Automatically buys items from zone vendors based on your shopping list. |
| Currency Reserved Amount | Amount of this currency to save and never spend automatically. |
| Currency Start Threshold | Triggers a vendor visit once you hold at least this much currency. |
| Keep Amount | Target stock to keep in your inventory. AOCCH buys enough to maintain this amount. |
| Buy Amount | One-time purchase quantity. Once bought, it won't keep re-buying. |
| Keep Buying | Continuously dumps extra currency into this item whenever available (only one item can have this set at a time). |
| Move Up | Move item higher in priority. |
| Move Down | Move item lower in priority. |
| Remove | Remove item from your shopping list. |
| Shopping Priority List | Order matters—items at the top get bought first. Items process Keep targets first, then Buy targets, then Keep Buying. |

## North Horn Status Options

These options are also available from the North Horn status window. Their
tooltip text remains consistent with the corresponding configuration settings above.

| Setting | Proposed tooltip |
| --- | --- |
| Enable Overworld Treasure Guide | Draws a visual guide line and marker in-game pointing to the closest coffer. Purely visual—doesn't automate movement. |
| Enable Anonymous Coffer Position Reporting | Anonymously sends confirmed coffer locations to help map out spawn points for North Horn. No personal, character, or chat data is ever sent. |
| Do Not Show This Update Again | Hides this update popup on launch until the next major update. |

## Excluded

The `DebugWindow` is intentionally excluded from this tooltip pass, including
its test inputs, diagnostic actions, automation controls, and preview controls.
