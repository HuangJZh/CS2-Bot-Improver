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
