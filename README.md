# Hitbox Viewer (Vintage Story 1.22)

Debug overlay that draws the runtime hit-test geometry of entities: vanilla collision/selection boxes and Combat Overhaul colliders.

- Alt+H cycles OFF -> TARGET -> RADIUS -> KEYPOINTS -> OFF. Chat: `.hbv off|target|radius|keypoints|labels|dump`.
- KEYPOINTS (0.2.0) highlights only Combat Overhaul damage zones whose multiplier is above 1.0, labelled zone + multiplier. Multipliers and the part-to-zone map come from the server's live CO data over the `hitboxviewer` channel (request driven, no broadcast). Without the mod on the server: one "hitboxviewer: no server data" line, nothing drawn.
- Universal mod; not required on either side. Build: `python -I src/build.py` (net10.0, references the game install and the CO lib).
