# CS2-Bullseye-Bot
CS2-Bullseye-Bot is a CounterStrikeSharp plugin that improves bots' aim in CS2.

![bullseye_2](https://github.com/user-attachments/assets/b658e64e-028c-42de-817c-1748fe0ca4f4)

# Commands

`bot_aim mixed`  
Bots select aiming spots flexibly based on situations (default)

`bot_aim head`  
Bots prioritize aiming at the head

`bot_aim body`  
Bots prioritize aiming at the torso

`bot_aim`  
Check the current aim mode
# Installation

1. Download the latest **BotAimImprover.zip** from [Releases](https://github.com/ed0ard/CS2-Bullseye-Bot/releases)

2. Extract the folder and upload it to `game/csgo/addons/counterstrikesharp/plugins` on your server
## If you find the plugin useful then please take the time to star⭐ the repository


## HuangJZh smoke-accuracy adjustment

Only smoke-obscured aim is changed: body-level inaccurate guesses are held for 0.4 seconds, with bounded lateral error and no live target-velocity prediction. Unobscured original aim behavior and the separately configured reaction times are retained. Smoke uses an approximate growing volume; actual hit rate has not been measured. Source baseline: ed0ard/CS2-Bullseye-Bot commit c5a2e0f402f151336c820b7efc1b895aa087a953.
