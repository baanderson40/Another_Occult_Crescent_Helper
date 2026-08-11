# Another Occult Crescent Helper

Another Occult Crescent Helper (AOCCH) is a Dalamud plugin for automating and monitoring activities in Final Fantasy XIV's Occult Crescent. It combines FATE and Critical Engagement farming with pot treasure, treasure coffer, movement, combat, recovery, and currency-shopping tools.

## Features

- Unified farm sessions for Critical Engagements and FATEs.
- Configurable CE and FATE selection, priorities, gearsets, arrival distances, and fallback timing.
- Pot FATE scheduling, spawn prediction, treasure hints, treasure-buff tracking, and coffer interaction.
- Optional North Horn bonus coffer searches with KI-selected area travel and Wind Current handling.
- Treasure hunting with hint interpretation, candidate search, dangerous-area travel, and optional treasure guide markers.
- Manual overworld coffer routes with optional starting route indexes.
- Automatic overworld coffer surveys and route starts based on silver and bronze thresholds.
- Optional anonymous submission of confirmed coffer observations to the public HTTPS endpoint.
- Route safety policies for knowledge-level threats, unsafe weather, Ashkin periods, and dangerous caverns.
- Optional Ninja gearset and Hide handling for dangerous treasure and coffer routes.
- Buff rotation and BossMod autorotation integration.
- Death recovery and return-to-base handling.
- Optional post-CE/FATE player revival during unified farm sessions using Chemist or White Mage.
- Instance-time management with configurable completion, treasure-hunt, and exit budgets.
- Currency shopping with reserve amounts, start thresholds, purchase quantities, priorities, and keep-buying behavior.
- Main, configuration, debug, dependency, and log windows.
- Territory and feature availability checks so unavailable data does not start automation.
- Panic stop controls for active farm activity.

The bundled data currently enables the listed automation features for **South Horn**. Feature availability depends on the active territory's data profile.

## Requirements

- [vnavmesh](https://github.com/awgil/ffxiv_navmesh)
- Either [BossMod](https://github.com/awgil/ffxiv_bossmod) or [BossmodReborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)

### Optional Rotations

- [RotationSolverReborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn)
- [Wrath](https://github.com/PunishXIV/WrathCombo)

## Installation

Add the following custom repository URL in Dalamud's plugin settings:

`https://raw.githubusercontent.com/baanderson40/dalamud_plugins/main/repo.json`

Then install **Another Occult Crescent Helper** from the plugin list.

The project is not a standalone application and must run inside Dalamud alongside Final Fantasy XIV.

## Getting Started

1. Enter a supported Occult Crescent territory.
2. Install and enable vnavmesh and BossMod or BossmodReborn.
3. Open AOCCH with `/aocch` if needed.
4. Configure the desired CE, FATE, pot, treasure, coffer, combat, and shopping options in the config window.
5. Start the unified farm session from the main window or with `/aocch start`.
6. Monitor the current activity in the main window.

## Configuration

The configuration window is divided into these tabs:

- **Critical Engagements**: enable CE farming, choose CE priority, and enable individual encounters.
- **FATEs**: enable FATE farming, choose FATE priority, configure dismount distance, and enable individual FATEs.
- **Pots**: configure pot farming, starting pot FATE, spawn timing, threat handling, dangerous travel, gearsets, and instance-time policy.
- North Horn bonus coffers are disabled by default because their three destination areas contain high-level aggro. When enabled, the plugin returns to Base Camp after the first coffer, uses another Magical Elixir, follows the KI-selected direction by aethernet, and resumes the geometric search in that area.
- **Treasure Coffers**: configure manual and automatic overworld coffer routes, guide markers, thresholds, route arrival, threat handling, dangerous travel, South Horn weather, and territory-specific Ashkin policies.
- **Shopping**: configure currency reserves, purchase thresholds, item priorities, keep amounts, one-off purchases, and keep-buying behavior.
- **Settings**: configure confirmed coffer observation sharing, autorotation overrides, target ranges, buff rotation, post-CE/FATE revival, Return usage, mounting distance, and interface scaling.

Confirmed coffer observation sharing is disabled by default. When enabled, the plugin transmits only territory, position, coffer type, version, and timestamp to the public coffer observation database for crowdsourcing.

Dangerous travel settings are experimental and intended for characters with suitable Knowledge progression. Review the tooltips in the configuration window before enabling them.

## Commands

All commands use the `/aocch` prefix.

| Command | Action |
| --- | --- |
| `/aocch` | Toggle the main window |
| `/aocch main` | Toggle the main window |
| `/aocch config` | Toggle the configuration window |
| `/aocch log` | Toggle the log window |
| `/aocch shopping` | Open shopping configuration |
| `/aocch start` | Start a unified CE/FATE farm session |
| `/aocch stop` | Stop the unified farm session |
| `/aocch coffer-start [index]` | Start the overworld coffer route, optionally at a one-based route index |
| `/aocch coffer-stop` | Stop the overworld coffer route |
| `/aocch panic` | Stop all farm activity |
| `/aocch help` | Show command help in chat |

The manual overworld coffer route cannot start while the unified farm session is running. Start commands are also blocked when required dependencies, territory data, or configured game actions are unavailable.

## Safety And Limitations

- Automation is limited to territories and features with validated data profiles.
- The plugin stops or blocks flows when the player changes territory, loses required data, or encounters an unrecoverable movement or interaction failure.
- Combat automation requires the relevant BossMod integration and suitable combat configuration.
- Pathing depends on vnavmesh being installed, available, and usable. Aethernet travel uses the in-game shard UI through ECommons callbacks.
- Dangerous travel, Hide usage, Ninja gearset swaps, weather handling, and high-level threat rules should be tested carefully before unattended use.
- The plugin does not guarantee successful interaction with game UI, combat, movement, or third-party plugin APIs after game updates.

Use the panic stop control or `/aocch panic` whenever immediate cancellation is required.

## Troubleshooting

1. Open the **Dependencies** window and confirm vnavmesh and BossMod are installed and available.
2. Open the **Debug** window to inspect territory data, current targets, FATEs, Critical Engagements, pot state, coffer state, movement, autorotation, recovery, and shopping state.
3. Open the **Log** window to review warnings, errors, state transitions, and interaction failures.
4. Confirm that the active territory supports the feature being started and that the relevant configuration tab has the feature enabled.
5. If a game or dependency update changes behavior, reproduce the issue with automation stopped and include the relevant debug and log output when reporting it.

## License

AOCCH is licensed under the [GNU Affero General Public License v3.0 or later](LICENSE.md).
