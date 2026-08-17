# `mc-blueprint/v1` reference

This reference is for the image-to-blueprint skill. It describes the repository's current parser and validator contract; it is not a command reference.

## Standard JSON

A standard document has:

- `format`: exactly `mc-blueprint/v1`;
- optional `project` string;
- `coordinateSystem`: normally `origin` as `[x, y, z]`, `north` such as `+z`, and `unit` `minecraft-block`;
- `bounds`: an inclusive `{ "from": [x, y, z], "to": [x, y, z] }` range;
- non-empty `phases` array.

Each phase has an `id`, optional `name`, an integer `order`, and an `operations` array. The supported operation types are:

### Fill

```json
{
  "id": "foundation-pad",
  "type": "fill",
  "from": [0, 64, 0],
  "to": [15, 64, 15],
  "block": "minecraft:stone"
}
```

Both endpoints are inclusive. Keep the volume inside document bounds. Avoid overlaps with every other fill and explicit placement because the current validator rejects implicit overwrites.

### Explicit blocks

```json
{
  "id": "entrance-details",
  "type": "blocks",
  "blocks": [
    { "pos": [4, 65, 4], "block": "minecraft:glass" }
  ]
}
```

Every position must be unique across the document and inside bounds. Use explicit blocks sparingly for openings, markers, irregular features, and details that cannot be represented as a safe non-overlapping fill.

## JSONL

The parser accepts one explicit placement per non-empty line. Use either a `pos` array/object or top-level `x`, `y`, `z`:

```json
{"phase":"details","pos":[4,65,4],"block":"minecraft:glass"}
{"phase":"details","x":5,"y":65,"z":4,"block":"minecraft:glass"}
```

JSONL bounds are derived from the positions and the phase order is assigned by first appearance. Use standard JSON when the blueprint needs meaningful explicit bounds, multiple fills, or a detailed coordinate-system declaration.

## Safe IDs and block IDs

The validator currently requires operation IDs to be non-empty ASCII text no longer than 128 characters and rejects duplicate IDs within a phase. Use stable slugs such as `main-floor-slab`, not generated timestamps.

Use lower-case namespaced IDs matching:

```text
minecraft:[a-z0-9_/.-]+
```

The parser can add the `minecraft:` prefix to an unnamespaced value, but emitting the namespace explicitly avoids ambiguity. Non-empty `states` are currently rejected by validation; omit them.

## Validation checklist

Before handoff, verify:

1. `format` and required fields are present.
2. Bounds and all ranges are valid, inclusive, and within the coordinate safety limit.
3. Every operation is within document bounds.
4. No fill/fill, fill/block, or duplicate block overlap exists.
5. Phase and operation IDs are deterministic and meaningful.
6. Block IDs are namespaced and syntactically valid.
7. Large ranges are intentionally split by the application batch planner.
8. The document parses offline and passes `BlueprintValidator`.
9. The application Dry Run is complete before any independently approved live operation.
