# Controlled fire-gate trial

This checks a stable output prohibition against real bot-generated input. It does not validate natural decision timing, aiming, recoil control, first-contact reaction, or player rank.

Use only an owned dedicated `-insecure` server bound to `127.0.0.1:27025`. Preserve the original plugin files and exact gameinfo bytes, then restore them after stopping the verified owned process. `bot_aim_test_gate` is registered only for this dedicated loopback configuration, accepts server-console calls only, and expires after at most five simulation seconds. Model/round/map reset clears the override.

## Establish a usable baseline

1. Load the full intended plugin set, the candidate Aim plugin and `tools/runtime-probe`. Verify versions and active difficulty. Do not treat a zero-shot server as a successful gate test.
2. End warmup with freeze time zero. Start capture and native recording **before** triggering the scene. `css_aim_probe scenario_open` uses two user-demo Dust2 ground positions; it records an independent live brush trace. `face_open` supplies at most two seconds of pre-contact view, released on native visual acquisition. Both are scripted fixture preparation.
3. Confirm native Attack requests and actual gun callbacks with the model disabled. Do not call `shoot`, `shoot_one`, or `force` for accepted actuator trials: input injection runs after suppression, and forced perception is not natural acquisition.
4. If bootstrap produces no input, investigate it separately. The October31 exploratory sequence used BotAI unloading/reloading and BotHider identity transitions. Shots continued after returning to player identity, so these data do **not** prove that player identity permanently prevents firing. Before accepted trials, BotAI was loaded again and identity was player. No identity configuration file was changed.

The helper reports controller flags and All/Aim/weapon/replay ownership. Numeric IDs work for `css_plugins unload/reload`; folder names and `#ID` were not equivalent in the tested build. Verify the resulting plugin list rather than assuming success.

## Timed output trial

With real acquisition already active and the actor holding the intended gun:

```text
bot_human_aim on
css_aim_probe refill
bot_aim_capture start 20
css_aim_probe record_start
bot_aim_test_gate block 3
```

Wait about4.5 seconds without tool/analysis gaps before the block command, then:

```text
bot_aim_test_gate status
css_aim_probe record_stop
bot_aim_capture stop
```

Run the command sequence as one bounded orchestration; otherwise the native ten-second recorder can finish before the prohibition begins. A refill is artificial and must not be used as fresh-weapon recoil evidence. The AK trial also locked the actor's primary slot; there was no injected Attack or movement override. The opponent eventually used a knife; its callbacks are excluded from the actor's gun counts.

```text
python tools/audit_gate_trial.py RUNTIME.jsonl MOTION.json glock glock-summary.json
python tools/audit_gate_trial.py RUNTIME.jsonl MOTION.json ak47 ak-summary.json
```

The audit requires matching pawn identity, weapon/override/suppression maintained, native coverage before and after the block, recorded native Attack requests, and recovery gunfire. It rejects empty-input and no-recovery trials. The steady window excludes the first100ms and final50ms for completed-record bracketing; **full-window and boundary callbacks remain in the output**. A command with held Attack is an input record, not necessarily a distinct trigger pull. The native recorder retains one pending command per physics boundary, not the complete command stream.

## October31 results and limits

Evidence: `E:/csdemo/bot-captures/native-gate-open-20261031`.

| Weapon | Steady-window records with native Attack | Post-simulation Attack | Steady gun callbacks | Recovery gun callbacks | Boundary callbacks |
|---|---:|---:|---:|---:|---:|
| Glock | 12 | 0 | 0 | 3 | 0 |
| AK | 6 | 0 | 0 | 9 | 1 |

The AK boundary callback was about15.6ms after prohibition began. This is **not** an instantaneous-gate acceptance result. Callback time is not exact shot-subtick time; the queued-shot/execution-order question remains open.

Native phase instrumentation was live (entry counts are saved at session end), but gun callbacks were delivered outside the observed Update/Upkeep scopes. Do not attribute a callback to a decision solely from its current model state. Current reaction/accuracy parameters were not recalibrated from this fixture. Common gameplay files were restored; candidates, runtime source snapshots and original records are retained separately.
