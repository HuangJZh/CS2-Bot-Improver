# Aim evidence pipeline

Raw data stays outside Git. Each demo export has a source SHA-256, parser version, recorded clock and stage flags.

```powershell
python -m pip install -r tools/requirements-aim.txt
python tools/export_match_evidence.py E:/csdemo/extracted --out E:/csdemo/evidence/matches
python tools/fetch_aim_geometry.py --release 2000930 --expected-patch 1.41.9.0 --out E:/csdemo/evidence/assets
python tools/probe_demo_visibility.py E:/csdemo/evidence/matches --assets E:/csdemo/evidence/assets/2000930
python -m unittest discover -s tools/tests
```

Current assets: Awpy data release 2000930, patch 1.41.9.0. The archive SHA-256 was checked against its release manifest. Other patches must not silently use these assets. Source: https://github.com/pnxenopoulos/awpy-data . Limitations: https://awpy.readthedocs.io/en/stable/visibility.html .

- `frames.parquet` preserves all ten players. Positions are pawn origins, not eyes/hitboxes.
- Event parquet files retain clock/phase fields and missing-clock flags. Parser-derived round boundaries are marked in the manifest.
- The latest zero-round match-start is a competitive-start candidate, not a replacement for auditing restarts.
- `damage-visibility-probes.csv` checks static geometry, alternative FOVs, approximate body points and smoke spheres at damage events. It does not contain verified first-visible timestamps. Penetration/smoke/blind kill flags support review.
- Raw flash fields must not be treated as perception delay without verifying their lifetime semantics. Radar spotting is not used as visual truth.
- Dynamic doors, exact hitboxes and smoke holes remain unmodeled. Damage events are not ground truth for unobstructed visibility.

## Bot telemetry

Plugin `2.1.8-human.2` adds local-console-only commands:

- `bot_aim_capture start 120`: capture up to120 seconds (10–300 allowed), stopping near100MB.
- `bot_aim_capture stop`: close and flush.
- `bot_aim_capture status`: show capture path.

Files go under plugin `telemetry/`. Bot-only JSONL records pawn generation, tick/time, actual recorded view/velocity/weapon, model angles/phase/fire permission, and fire/hurt/death callbacks. No network upload; capture is off by default and does not change aim parameters.

Event-callback angles are not guaranteed exact subtick shot angles. Server observations and planning can occur at different times within a tick. Runtime capture validation remains pending.

## Full-tick candidate contacts and shot-state comparison

```powershell
python tools/compare_shot_state.py E:/csdemo/evidence/matches
python tools/build_contact_candidates.py E:/csdemo/evidence/matches --assets E:/csdemo/evidence/assets/2000930 --out E:/csdemo/evidence/contacts-v2
python tools/enrich_contact_candidates.py E:/csdemo/evidence/matches --candidate-root E:/csdemo/evidence/contacts-v2
python tools/audit_aim_capture.py E:/csdemo/bot-captures/controlled-20261031
```

Full scans currently default to NiKo, kyousuke, TeSeS and m0NESY as observers; all opponents are considered. Pass `--viewers` without names for all observers. Contacts bridge at most250ms of estimated visibility loss; deaths, recording gaps, phases and rounds end or censor them. Row round index is zero-based. Fire events remain unattributed to a target unless separately reviewed. First-fire/damage deltas are candidate metrics, not validated reaction time or TTD.

Raw view snapshots can repeat stale network state. The15.625ms server tick interval is not proof of a fresh view update at every tick. `fire_bullets` has separate recorded angles, origins, spread, inaccuracy and recoil index; these must not be conflated with raw mouse input or with the network view.

Runtime fixture source lives under `tools/runtime-probe`. It only registers commands for a dedicated server explicitly using port27025. Its scene, perception handles and attack inputs are scripted; it does not establish natural AI behavior. Temporary binaries were archived outside the game after testing.

## Experimental direct view

`DirectViewControl` defaults to false and was restored off after testing. The installed native reference at BotController bb19124 declares `UpdateLookAngles` as void(bot*) and `SetEyeAngles` as void(pawn*,float*). The adapter now uses the matching arity.

In a scripted lateral-step fixture, point-target-only control showed persistent recorded-vs-planned yaw differences. The experimental post-hook drives engine eye angles and restores the temporary controller flag in a finally block, following the installed native replay helper. Recorded angles tracked the plan in this fixture, including fire/hurt/death callbacks. This does NOT validate recoil compensation, natural engagements, or player-rank strength. Direct control overrides the native final-view path; it must not be enabled as final gameplay until those checks pass.

Native reference: https://github.com/XBribo/CS2-Bot-Controller/blob/bb19124/src/features/controller/BotController.cpp .

The second contact version explicitly uses freeze-end to round-end windows and prioritizes recorded deaths at same-tick round transitions. Physical visibility caches are keyed by code and source-data hashes and checked for coverage; cached replay was verified to reproduce candidate rows exactly. That test proves reproducibility, not visual ground truth.

```powershell
python tools/make_contact_review_queue.py E:/csdemo/evidence/matches --candidate-root E:/csdemo/evidence/contacts-v2 --out E:/csdemo/evidence/contact-review-queue-v2.csv
```

Standing/crouched/transition descriptions are not causal comparisons unless weapon, movement and recoil stage are controlled. The first recoil fixture allowed native crouching; that run is deliberately not used to fit a recoil estimator. A later paired stance protocol must suppress jump/movement and control Duck separately, while final gameplay retains appropriate crouch/spray coordination.

Installed original BotState 1.10.5 has two conflicting mechanisms: per-shot random crouch decisions and weapon-dependent mandatory speed changes after fire. Its installed hash matches the preserved original backup, and installed IL method references were inspected. A burst-stable posture policy is now source-built and tested but NOT installed. Forced velocity remains unchanged in that candidate and requires a separate ownership fix; don't assume BotSupport75/85/95% is currently the only stopping mechanism.

## Stance actuator validation

`audit_burst_posture.py` reads actual firing spans and reports intent switches; it is not a probability/skill verifier. BotState1.10.5-stance.1 was run in a loopback fixture with movement/jump controlled but observer Duck left native. Two standing and one crouching30-shot AK spans had zero intra-span intent switches; crouch released after the pause. The wrong-weapon TEC9 attempt was excluded. Manual refills were used, so these runs are not fresh-weapon recoil controls. Evidence and hashes: E:/csdemo/bot-captures/stance-20261031/stance-validation.json .

Final gameplay retains crouch/spray coordination. `stabilize_stand` and `stabilize_crouch` are fixture-only controls for a subsequent paired physical experiment, not final stance policy. The original mandatory velocity changes remain in the stance build and still conflict with BotSupport probability; ownership must be fixed separately.
