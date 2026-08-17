# Image-to-geometry workflow

## Evidence ledger

Maintain a table with one row per proposed feature:

| Feature | Evidence | Confidence | Geometry decision | Open question |
|---|---|---|---|---|
| Main mass | clearly visible facade footprint | high/medium/low | rectangular approximation with stated corners | rear extent? |
| Floor level | user-provided height or repeated visible bands | high/medium/low | integer Y planes | exact floor-to-floor height? |
| Roof | visible silhouette | high/medium/low | simple roof volume or sparse blocks | hidden slope/material? |

Use `observed` only for evidence in the image, `user-provided` for measurements and orientation, `assumed` for modelling choices, and `unresolved` for occluded or unavailable information.

## Scale

A single perspective image does not establish a reliable world scale. Prefer, in order:

1. a survey or CAD dimension supplied by the user;
2. a known building/site length visible in the image;
3. a known door, stair, vehicle, or other reference object, with an uncertainty range;
4. a user-approved block-per-metre convention.

Document the equation, for example:

```text
block_length = measured_meters × approved_blocks_per_meter
```

Round only after documenting the unrounded value. Preserve the uncertainty as a note and do not imply that rounded coordinates are survey-grade.

## Perspective and occlusion

Do not compare raw pixel widths at different depths. Do not use the image's top edge as north. Do not invent a rear elevation, hidden courtyard, floor plan, or material schedule. For an oblique render, model major visible masses first and mark unseen sides as conceptual. If the image is unavailable to a visual reader, return a limitation report rather than guessing.

## Geometry decomposition

Break the scene into separate, reviewable layers:

- site and ground treatment;
- foundations and plinths;
- main building masses;
- floor slabs and vertical levels;
- facade walls and known partitions;
- roofs/parapets;
- doors/windows and other openings;
- corridors, bridges, and canopies;
- roads, paths, courtyards, water, planting, and lighting.

A layer may be omitted when it is not supported by evidence. Name conceptual approximations in phase and operation names.

## Operation design

Use rectangular `fill` for homogeneous solids. Use explicit `blocks` for sparse details. Because the current validator rejects overlap, reserve each coordinate once and make void/opening geometry explicit by modelling only the surrounding solids rather than filling through the opening. Split a complex outline into disjoint rectangles or use sparse blocks; never rely on execution order to overwrite an earlier fill.

## Review gates

Before handoff, ask the user to confirm:

- origin, north, ground Y, and scale;
- whether terrain is changed;
- target fidelity and material substitutions;
- whether conceptual assumptions are acceptable;
- bounds and maximum intended size.

Then run offline parsing/validation and the application's Dry Run. The skill ends there. Live capability probes and construction are a separate, explicitly confirmed operation of JdMcBuilder.
