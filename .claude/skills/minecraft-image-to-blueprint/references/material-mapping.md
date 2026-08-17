# Conservative material mapping

Material mapping is an explicit approximation, not an inference of the real building's bill of materials. Use the user's requested palette when available and record each substitution in the assumptions report.

| Visual or functional role | Conservative default | Notes |
|---|---|---|
| Foundation / plinth | `minecraft:stone` | Use only where a solid foundation is intended. |
| Structural wall | `minecraft:stone_bricks` or `minecraft:bricks` | Pick one palette and keep it consistent. |
| Light facade | `minecraft:quartz_block` | A visual approximation, not proof of real material. |
| Glass opening | `minecraft:glass` | Use sparse blocks around an opening; avoid filling through an intended void. |
| Dark roof | `minecraft:deepslate_tiles` | State that roof color/texture is conceptual when not confirmed. |
| Timber accent | `minecraft:oak_planks` | Do not imply structural timber without evidence. |
| Road / paving | `minecraft:stone` or `minecraft:gray_concrete` | Clarify whether the layer is decorative or terrain-changing. |
| Grass / landscape | `minecraft:grass_block` | Use only when the requested scope includes site treatment. |
| Water feature | `minecraft:water` | Treat fluids as a separate, explicitly approved phase. |

## Rules

- Always emit a fully namespaced, lower-case block ID.
- Do not use a material name as an executable instruction.
- Do not add block states, NBT, commands, selectors, or arbitrary server syntax; the current validator rejects non-empty `states`.
- Avoid mixing many materials merely to imitate uncertain pixels.
- Record the chosen default, alternatives considered, and the user's approval status.
- If the user asks for exact real-world materials, request a schedule or reliable source instead of guessing from color.
