# CS2 Bot Improver — HuangJZh Independent Edition

**Panel 1.0.0 · Gameplay Custom 8.60 · Base provenance v1.4.8**

[简体中文 — full guide](../README.md) · **English**

This personal fork of [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) maintains custom aim, shooting, team support, economy, utility and navigation behavior for local bot matches and private games. The maintained branch is [`custom/independent`](https://github.com/HuangJZh/CS2-Bot-Improver/tree/custom/independent).

## Current behavior

- Three aim profiles with perception delay, flicking, tracking, first-shot alignment and burst control; MIXED / HEAD / BODY modes.
- Human-side bot strength coefficient 0.75, with 1–2 opposing elites receiving a 20–30% profile boost. These are tuning coefficients, not win-rate guarantees.
- Preserve carried weapons; prioritize a primary weapon, weapon requests, then utility. Normal purchases exclude AUG and SG553.
- Team sniper purchase quotas: one for T, up to two for CT. Carried and human-held snipers count; existing weapons are preserved.
- Chat-wheel weapon requests choose one eligible nearby donor, prioritizing funds. Random affordable AWP / AK / M4 selection, cheaper rifle / SMG fallback, then transfer of a nearby teammate's most valuable carried primary if no purchase is affordable.
- Actual grenade purchasing and inventory consumption, including a shared Molotov/incendiary category.
- Team route allocation using the game's navigation areas, native movement and lightweight round-history feedback.
- Post-plant integration with Valve's Retakes behavior-tree modules in supported classic modes, while retaining custom combat control.

Ordinary-enemy base reaction fields are approximately **Low 150 ms / Medium 100 ms / High 75 ms**. These fields are not measured visibility-to-shot times. Perception, view angle, aim motion, weapon readiness and profile modifiers also affect firing. The calibration is an estimated Aim Rush comparison, not a measured player rank or proven equivalence.

Latest gameplay change: [Custom 8.60](JUMP-AUDIT-8.60-20261115.md) audits nine elevation transitions across Dust2, Mirage and Inferno, with bounded assistance toward the native movement destination and traversal ownership fixes. Opening knife travel and large-turn firing guards are retained.

## Using the current installation

Open the existing desktop shortcut to `Panel.exe`, confirm **独立版 1.0.0** in its header and green directory/file indicators, select **Bot Mode**, choose the presets, and launch CS2. A yellow indicator means a restart is required. Use **Online Mode** to restore the normal game configuration.

The current package updates the panel of an existing validated custom installation; it is not a full plugin installer. A source checkout alone is not runnable. There is currently no complete independent package for a fresh installation. Do not overwrite the custom installation with an upstream release package.

The validated environment is **Windows local hosting**, with CS2 build 26598758 and a pinned native compatibility patch. Custom Linux support has not been validated. Consult the [dependency lock](../dependencies/base-v1.4.8.lock.json) before upgrading.

## Commands

| Command | Purpose |
| --- | --- |
| `bot_aim mixed` / `head` / `body` | Choose aim mode. |
| `bot_nades off` / `less` / `normal` / `more` / `max` | Choose utility policy; real inventory is still required. |
| `css_bot_request auto` | Request a weapon; also available through the chat wheel. |
| `bot_human_aim status` | Custom aim status. |
| `bot_team_nav status` | Navigation status. |
| `bot_route_learning status` | Route-learning status. |
| `bot_retake_ai status` | Retakes bridge status. |
| `bot_aim_capture start 60` / `bot_aim_capture stop` | Local diagnostic capture. |

Diagnostic commands are host-only. Team command blocks are in [Commands.txt](../Commands.txt) and the panel's command browser.

## Maintenance and source boundaries

The panel no longer checks upstream versions or displays an update button. Its project link opens this fork. Base-component updates require manual review, compatibility checks and a recovery point.

The upstream panel's Rust backend source is not published. Independent panel 1.0.0 uses a reproducible UI overlay on the exact validated 1.4.8 binary, retaining native instructions and configuration compatibility. The frontend no longer calls the dormant native update-query handler. This is not a full panel rebuild from source.

See the [independent version notes](INDEPENDENT-VERSION-20261115.md), [base update procedure](BASE-SOFTWARE-UPDATES.md), [calibration notes](AIMRUSH-ESTIMATE-PRESETS-20261114.md), and [full guide and credits](../README.md).

Original credits and licences remain: [AGPL-3.0](../LICENSE), except `Panel/`, which uses [PolyForm Strict 1.0.0](../Panel/LICENSE).
