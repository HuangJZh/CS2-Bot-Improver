# CS2-Bot-Buy
CS2-Bot-Buy is a plugin based on CounterStrikeSharp that allows bots to buy everything and overhauls their economy logic.

## Features
1. Allow bots to purchase all weapons, including armor and defuse kits.

2. Remove the restriction preventing bots from buying in the first round of each half (including overtime).

3. Enable bots to drop weapons for teammates with less than $2800 when they have sufficient money.

4. Overhaul bots’ economy management strategy.

## Installation
1. Download the latest BotBuy.zip from [Releases](https://github.com/ed0ard/CS2-Bot-Buy/releases)

2. Extract the folder and upload it to game/csgo/addons/counterstrikesharp/plugins on your server

3. Restart your server

(**Tip: Works better when `bot_eco_limit` is set to 2800**)
## Credits
Inspired by [droyer57](https://github.com/droyer57)


## Local purchase repair (2026-11-11)

Upstream baseline: ed0ard/CS2-Bot-Buy `11884e0564b8b57bee5f4729a5fca781bd5e6749` (1.0.14). This directory is now a normal customized source tree, rather than an automatically advancing submodule.

- Primary ownership scans the entire carried inventory, including MAC-10, SG553 and auto-snipers. Holding a knife/pistol never means the player lacks a primary.
- Snapshot the actual primaries at round start. BotController buy plans for these bots contain only armor/kit aliases, so native buying cannot throw away a survivor's primary to rebuy it. Plans are released on round end, disconnect, map change and unload.
- Plugin purchases refuse a second primary; weapon swaps refuse identical items and carried-over primaries. Failed entity creation never charges money. Delayed buy callbacks expire with their originating round.
- Remove the random rich-bot replacement of good guns with an M249/auto-sniper. AUG/SG553 cannot be purchase targets; only newly acquired scoped rifles may be converted to team main rifles.
- All three BotProfile difficulty templates exclude AUG/SG553. Main rifles and AWP precede budget guns; sniper profiles retain AWP first. Explicit custom bot_loadout modes keep their separate behavior.
- Wheel donation fallbacks live in BotSupport and also exclude AUG/SG553.

Build against the installed CSS 1.0.379 API and matching BotController shared API with `-p:CounterStrikeSharpApiPath=<api-directory>`. Run `dotnet run --project tests/PurchaseTests.csproj` and `python tools/tests/test_purchase_profiles.py` from the repository root for regression checks.
