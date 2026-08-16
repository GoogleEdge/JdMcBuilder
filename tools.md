# MCC MCP 工具完整参考文档

> 生成时间：2026-08-15
> 适用环境：Minecraft Console Client (MCC) 内置 MCP 服务器
> 当前会话能力快照：sessionStatus=enabled, chatAndCommands=enabled, movement=enabled, inventory=enabled, entityWorld=enabled

---

## 目录

1. [会话与连接类](#1-会话与连接类)
2. [世界状态与方块类](#2-世界状态与方块类)
3. [玩家状态类](#3-玩家状态类)
4. [实体类](#4-实体类)
5. [物品与掉落物类](#5-物品与掉落物类)
6. [库存与容器类](#6-库存与容器类)
7. [移动与视角类](#7-移动与视角类)
8. [动作交互类](#8-动作交互类)
9. [事件与信息类](#9-事件与信息类)
10. [MCC 内部命令 (mcc_run_internal_command)](#10-mcc-内部命令-mcc_run_internal_command)
11. [通用调用约定与最佳实践](#11-通用调用约定与最佳实践)

---

## 1. 会话与连接类

### mcc_session_status
- **用途**：获取当前 MCC 会话和功能状态（连接状态、启用的能力）。
- **参数**：无
- **调用示例**：`mcc_session_status()`
- **返回示例**：
  ```json
  {"host":"127.0.0.1","port":25565,"username":"MCCBot","protocolVersion":776,
   "terrainEnabled":true,"inventoryEnabled":true,"entityEnabled":true,
   "location":{"x":-12.3,"y":207,"z":603.79}}
  ```
- **注意事项**：当连接状态、能力启用情况或功能可用性不确定时，应首先调用此工具。

### mcc_server_info
- **用途**：获取活跃 MCC 服务器连接信息和当前 TPS（每秒 tick 数）。
- **参数**：无
- **调用示例**：`mcc_server_info()`

### mcc_disconnect
- **用途**：断开 MCC 与当前服务器的连接，但不退出进程。
- **参数**：无
- **调用示例**：`mcc_disconnect()`

### mcc_quit_client
- **用途**：干净退出 MCC 客户端进程。
- **参数**：无
- **调用示例**：`mcc_quit_client()`
- **注意事项**：官方指引明确要求停止 MCC 时使用此工具，**不要**通过聊天发送裸 `quit` 或 `exit` 命令。

### mcc_loaded_bots
- **用途**：列出当前已加载的 MCC bots 和脚本（当 bot/脚本存在可能影响观察行为时使用）。
- **参数**：无
- **调用示例**：`mcc_loaded_bots()`

---

## 2. 世界状态与方块类

### mcc_world_state
- **用途**：获取当前世界状态、区块加载进度、最近观测到的运行时时间/天气。
- **参数**：无
- **调用示例**：`mcc_world_state()`
- **注意事项**：当区块加载、维度或时间/天气准备状态影响计划时，应使用它验证世界状态假设。

### mcc_chunk_status
- **用途**：获取玩家位置或指定世界坐标处的区块加载状态。
- **参数**：
  - `x` (number, 可选)：世界 X 坐标
  - `y` (number, 可选)：世界 Y 坐标
  - `z` (number, 可选)：世界 Z 坐标
- **调用示例**：
  - `mcc_chunk_status()`
  - `mcc_chunk_status(x=100, y=64, z=-200)`

### mcc_world_block_at
- **用途**：获取世界坐标处的方块信息。
- **参数**：
  - `x` (integer, 必填)：X 坐标
  - `y` (integer, 必填)：Y 坐标
  - `z` (integer, 必填)：Z 坐标
- **调用示例**：`mcc_world_block_at(x=100, y=64, z=-200)`

### mcc_block_types_list
- **用途**：列出已知的 MCC 方块类型名称，支持可选过滤。
- **参数**：
  - `filter` (string, 可选)：名称过滤条件
  - `maxCount` (integer, 可选，默认 500)：最大返回数量
- **调用示例**：
  - `mcc_block_types_list()`
  - `mcc_block_types_list(filter="chest")`

### mcc_blocks_find
- **用途**：按方块名称/类型查询或方块 ID 查找附近方块。
- **参数**：
  - `query` (string, 可选)：方块名称/类型查询
  - `radius` (integer, 可选，默认 6)：搜索半径
  - `maxCount` (integer, 可选，默认 200)：最大返回数量
  - `exactMatch` (boolean, 可选，默认 false)：是否精确匹配
- **调用示例**：
  - `mcc_blocks_find(query="chest", radius=10)`
  - `mcc_blocks_find(query="oak_log", radius=8, exactMatch=true)`

### mcc_block_scan
- **用途**：扫描玩家周围方块。
- **参数**：
  - `radius` (integer, 可选，默认 3)：扫描半径
  - `maxCount` (integer, 可选，默认 200)：最大返回数量
  - `materialFilter` (string, 可选)：材质过滤
- **调用示例**：`mcc_block_scan(radius=5, materialFilter="stone")`

### mcc_raycast_block
- **用途**：从玩家当前视角发射射线，返回命中的第一个非空气方块。
- **参数**：
  - `maxDistance` (number, 可选，默认 8)：最大检测距离
  - `includeNeighbors` (boolean, 可选，默认 false)：是否包含邻居方块
- **调用示例**：`mcc_raycast_block(maxDistance=8)`
- **注意事项**：视角相关的方块交互在操作前后都应使用 `mcc_raycast_block` 或 `mcc_world_block_at` 验证精度。

### mcc_materials_list
- **用途**：列出已知的 MCC 材料名称，支持可选过滤。
- **参数**：
  - `filter` (string, 可选)：名称过滤条件
  - `maxCount` (integer, 可选，默认 500)：最大返回数量
- **调用示例**：`mcc_materials_list(filter="diamond")`

### mcc_signs_find
- **用途**：查找附近文字与请求文本完全匹配或包含的告示牌。
- **参数**：
  - `text` (string, 必填)：要匹配的文本
  - `exactMatch` (boolean, 可选，默认 false)：是否完全匹配
  - `radius` (integer, 可选，默认 16)：搜索半径
  - `maxCount` (integer, 可选，默认 50)：最大返回数量
  - `includeBackText` (boolean, 可选，默认 true)：是否包含告示牌背面文字
- **调用示例**：`mcc_signs_find(text="shop", radius=32)`

---

## 3. 玩家状态类

### mcc_player_state
- **用途**：获取当前受控玩家的状态。
- **参数**：无
- **调用示例**：`mcc_player_state()`

### mcc_player_stats
- **用途**：获取当前受控玩家的统计信息、朝向（yaw/pitch）和位置。
- **参数**：无
- **调用示例**：`mcc_player_stats()`
- **注意事项**：移动完成后必须用此工具确认 `arrived=true` 或验证新位置；热栏选择后也用它确认选中槽位/手持状态。

### mcc_status_effects
- **用途**：仅获取当前玩家的活跃状态效果（不推断）。
- **参数**：无
- **调用示例**：`mcc_status_effects()`

### mcc_players_list
- **用途**：列出当前已知的在线玩家。
- **参数**：无
- **调用示例**：`mcc_players_list()`

### mcc_players_detailed
- **用途**：列出在线玩家详情：UUID、延迟、游戏模式、已跟踪坐标。
- **参数**：
  - `includeSelf` (boolean, 可选，默认 false)：是否包含自己
  - `includeCoordinates` (boolean, 可选，默认 true)：是否包含坐标
- **调用示例**：`mcc_players_detailed(includeSelf=true)`

### mcc_player_locate
- **用途**：按名称定位玩家实体，返回精确坐标（如可获取）。
- **参数**：
  - `playerName` (string, 必填)：玩家名称
  - `includeSelf` (boolean, 可选，默认 false)：是否包含自己
- **调用示例**：`mcc_player_locate(playerName="Zarko")`
- **注意事项**：面向玩家相关任务时，移动前优先用此工具或 `mcc_players_detailed` 确认目标最新位置。

### mcc_player_nearby
- **用途**：检查是否有玩家（或指定玩家）在附近。
- **参数**：
  - `playerName` (string, 可选)：指定玩家名称；为空则检查任意玩家
  - `radius` (number, 可选，默认 32)：检测半径
  - `includeSelf` (boolean, 可选，默认 false)：是否包含自己
- **调用示例**：
  - `mcc_player_nearby(radius=16)`
  - `mcc_player_nearby(playerName="Zarko", radius=10)`

---

## 4. 实体类

### mcc_entities_list
- **用途**：列出已跟踪实体，支持类型和半径过滤。
- **参数**：
  - `maxCount` (integer, 可选，默认 100)：最大返回数量
  - `typeFilter` (string, 可选)：实体类型过滤
  - `radius` (number, 可选，默认 0)：半径过滤（0 表示不限制）
- **调用示例**：
  - `mcc_entities_list(maxCount=50)`
  - `mcc_entities_list(typeFilter="zombie", radius=32)`

### mcc_entities_query
- **用途**：查询已跟踪实体。
- **参数**：
  - `maxCount` (integer, 可选，默认 50)：最大返回实体数
- **调用示例**：`mcc_entities_query(maxCount=50)`

### mcc_entity_types_list
- **用途**：列出已知的 MCC 实体类型名称，支持可选过滤。
- **参数**：
  - `filter` (string, 可选)：名称过滤条件
  - `maxCount` (integer, 可选，默认 500)：最大返回数量
- **调用示例**：`mcc_entity_types_list(filter="cow")`

### mcc_entity_nearest
- **用途**：返回满足过滤条件的最邻近已跟踪实体。
- **参数**：
  - `typeFilter` (string, 可选)：实体类型过滤
  - `nameFilter` (string, 可选)：实体名称过滤
  - `radius` (number, 可选，默认 64)：搜索半径
  - `includePlayers` (boolean, 可选，默认 true)：是否包含玩家
- **调用示例**：`mcc_entity_nearest(typeFilter="creeper", radius=32)`
- **注意事项**：目标可能移动或消失时，攻击/交互前应用它或 `mcc_entity_info` 验证目标。

### mcc_entity_info
- **用途**：获取单个已跟踪实体的详细信息。
- **参数**：
  - `entityId` (integer, 必填)：实体 ID
  - `includeMetadata` (boolean, 可选，默认 false)：是否包含元数据
  - `includeEquipment` (boolean, 可选，默认 true)：是否包含装备
  - `includeEffects` (boolean, 可选，默认 true)：是否包含效果
- **调用示例**：`mcc_entity_info(entityId=123, includeMetadata=true)`

### mcc_entity_interact
- **用途**：与已跟踪实体交互。
- **参数**：
  - `entityId` (integer, 必填)：实体 ID
  - `interaction` (string, 可选，默认 "Interact")：交互类型
  - `hand` (string, 可选，默认 "MainHand")：使用的手
- **调用示例**：`mcc_entity_interact(entityId=123, interaction="Interact", hand="MainHand")`

### mcc_entity_attack
- **用途**：显式攻击已跟踪实体。
- **参数**：
  - `entityId` (integer, 必填)：实体 ID
- **调用示例**：`mcc_entity_attack(entityId=123)`

---

## 5. 物品与掉落物类

### mcc_items_list
- **用途**：列出附近掉落物实体，支持按物品类型过滤。
- **参数**：
  - `itemType` (string, 可选)：物品类型过滤
  - `radius` (number, 可选，默认 32)：搜索半径
  - `maxCount` (integer, 可选，默认 100)：最大返回数量
- **调用示例**：`mcc_items_list(itemType="Diamond", radius=16)`
- **注意事项**：丢弃物品后可用它确认附近存在掉落物实体；拾取后用它验证剩余情况。

### mcc_items_pickup
- **用途**：移动到并拾取附近指定类型的掉落物。
- **参数**：
  - `itemType` (string, 必填)：物品类型（枚举名，如 `Diamond`）
  - `radius` (number, 可选，默认 32)：搜索半径
  - `maxItems` (integer, 可选，默认 20)：最大拾取数量
  - `allowUnsafe` (boolean, 可选，默认 false)：允许不安全移动（如掉落、触火）
  - `timeoutMs` (integer, 可选，默认 0)：超时（毫秒）
- **调用示例**：`mcc_items_pickup(itemType="Apple", radius=16, maxItems=10)`
- **注意事项**：拾取完成不代表拿到物品，需用库存快照或掉落物列表复核。

### mcc_inventory_snapshot
- **用途**：获取一个库存的快照。
- **参数**：
  - `inventoryId` (integer, 可选，默认 0)：库存 ID，0 为玩家库存
- **调用示例**：
  - `mcc_inventory_snapshot()`（玩家库存）
  - `mcc_inventory_snapshot(inventoryId=0)`
- **注意事项**：丢弃/转移物品后必须用新快照复核，不要只凭意图判断。

### mcc_inventory_search
- **用途**：在玩家库存和（可选的）打开的容器中搜索匹配物品。
- **参数**：
  - `query` (string, 必填)：搜索关键词
  - `maxCount` (integer, 可选，默认 100)：最大返回数量
  - `exactMatch` (boolean, 可选，默认 false)：是否精确匹配
  - `includeContainers` (boolean, 可选，默认 true)：是否包含打开的容器
- **调用示例**：`mcc_inventory_search(query="diamond")`

### mcc_inventory_drop_item
- **用途**：从指定库存中丢弃精确数量的指定类型物品。
- **参数**：
  - `itemType` (string, 必填)：物品类型枚举名（如 `Diamond`）
  - `count` (integer, 必填)：丢弃数量
  - `inventoryId` (integer, 可选，默认 0)：库存 ID，0 为玩家库存
  - `preferStack` (boolean, 可选，默认 false)：优先从较大堆叠丢弃
- **调用示例**：`mcc_inventory_drop_item(itemType="Dirt", count=1)`
- **注意事项**：丢弃后立即用 `mcc_inventory_snapshot` 复核。物品离开自己库存 ≠ 对方收到，"给了某玩家"的表述必须有更强证据。

### mcc_inventory_window_action
- **用途**：对库存槽位执行窗口操作（底层操作）。
- **参数**：
  - `inventoryId` (integer, 必填)：库存 ID
  - `slotId` (integer, 必填)：槽位 ID
  - `actionType` (string, 必填)：WindowActionType 枚举名，如 `LeftClick`、`ShiftClick`
- **调用示例**：`mcc_inventory_window_action(inventoryId=0, slotId=5, actionType="LeftClick")`
- **注意事项**：箱子/容器工作优先用 `mcc_container_open_at`、`mcc_container_deposit_item`、`mcc_container_withdraw_item`，而不是此底层工具。

### mcc_inventories_list
- **用途**：列出当前打开的库存和 MCC 已知容器。
- **参数**：无
- **调用示例**：`mcc_inventories_list()`

### mcc_select_item
- **用途**：按物品类型选择快捷栏物品，不重排库存内容。
- **参数**：
  - `itemType` (string, 必填)：物品类型
  - `preferLowestSlot` (boolean, 可选，默认 true)：优先选择编号更小的槽位
- **调用示例**：`mcc_select_item(itemType="Diamond_Pickaxe")`
- **注意事项**：目标是"现在就拿着对的物品"时用它，而不是手动切槽。返回成功还需用 `mcc_player_stats` 或新库存读取确认选中状态。

### mcc_change_hotbar_slot
- **用途**：改变活跃快捷栏槽位。
- **参数**：
  - `slot` (integer, 必填)：槽位编号 1-9
- **调用示例**：`mcc_change_hotbar_slot(slot=1)`

---

## 6. 库存与容器类

### mcc_container_open_at
- **用途**：在世界坐标打开可交互的容器方块，并等待容器库存出现。
- **参数**：
  - `x` (integer, 必填)：X 坐标
  - `y` (integer, 必填)：Y 坐标
  - `z` (integer, 必填)：Z 坐标
  - `timeoutMs` (integer, 可选，默认 0)：超时（毫秒）
  - `closeCurrent` (boolean, 可选，默认 true)：是否先关闭当前打开的容器
- **调用示例**：`mcc_container_open_at(x=11000, y=64, z=11021)`

### mcc_container_close
- **用途**：关闭打开的非玩家容器。
- **参数**：
  - `inventoryId` (integer, 可选，默认 -1)：容器库存 ID；-1 表示关闭当前活动的非玩家容器
  - `timeoutMs` (integer, 可选，默认 0)：超时（毫秒）
- **调用示例**：`mcc_container_close(inventoryId=-1)`

### mcc_container_deposit_item
- **用途**：将精确数量的物品从玩家库存移入打开的容器，并验证转移。
- **参数**：
  - `itemType` (string, 必填)：物品类型枚举名（如 `Diamond`）
  - `count` (integer, 必填)：移入容器的数量
  - `inventoryId` (integer, 可选，默认 -1)：容器库存 ID；-1 为当前活动容器
  - `preferLargestStack` (boolean, 可选，默认 true)：优先从较大堆叠取物
- **调用示例**：`mcc_container_deposit_item(itemType="Diamond", count=5, inventoryId=-1)`
- **注意事项**：转移后需用结果计数复核，必要时用新库存快照或 `mcc_recent_events` 确认。

### mcc_container_withdraw_item
- **用途**：将精确数量的物品从打开的容器移入玩家库存，并验证转移。
- **参数**：
  - `itemType` (string, 必填)：物品类型枚举名（如 `Diamond`）
  - `count` (integer, 必填)：移入玩家库存的数量
  - `inventoryId` (integer, 可选，默认 -1)：容器库存 ID；-1 为当前活动容器
  - `preferLargestStack` (boolean, 可选，默认 true)：优先从较大源堆叠取物
- **调用示例**：`mcc_container_withdraw_item(itemType="Diamond", count=3, inventoryId=-1)`

---

## 7. 移动与视角类

### mcc_move_to
- **用途**：请求寻路移动到世界坐标并验证到达。
- **参数**：
  - `x` (number, 必填)：目标 X
  - `y` (number, 必填)：目标 Y
  - `z` (number, 必填)：目标 Z
  - `allowUnsafe` (boolean, 可选，默认 false)：允许不安全移动（掉落、触火等）
  - `allowDirectTeleport` (boolean, 可选，默认 false)：允许直接传送
  - `maxOffset` (integer, 可选，默认 0)：最大到达偏移
  - `minOffset` (integer, 可选，默认 0)：最小到达偏移
  - `timeoutMs` (integer, 可选，默认 0)：超时（毫秒）
- **调用示例**：`mcc_move_to(x=100, y=64, z=-200, maxOffset=1)`
- **注意事项**：移动请求被接受 ≠ 移动完成。必须确认 `arrived=true` 或用新状态读取验证新位置。

### mcc_move_to_player
- **用途**：定位已跟踪玩家实体，请求寻路移动并验证到达。
- **参数**：
  - `playerName` (string, 必填)：玩家名称
  - `allowUnsafe` (boolean, 可选，默认 false)
  - `allowDirectTeleport` (boolean, 可选，默认 false)
  - `maxOffset` (integer, 可选，默认 0)
  - `minOffset` (integer, 可选，默认 0)
  - `timeoutMs` (integer, 可选，默认 0)
- **调用示例**：`mcc_move_to_player(playerName="Zarko", maxOffset=2)`

### mcc_path_preview
- **用途**：计算到目标世界坐标的路径预览，不实际移动。
- **参数**：
  - `x` (number, 必填)
  - `y` (number, 必填)
  - `z` (number, 必填)
  - `allowUnsafe` (boolean, 可选，默认 false)
  - `maxOffset` (integer, 可选，默认 0)
  - `minOffset` (integer, 可选，默认 0)
  - `timeoutMs` (integer, 可选，默认 0)
  - `maxWaypoints` (integer, 可选，默认 128)：最大路径点数量
- **调用示例**：`mcc_path_preview(x=100, y=64, z=-200)`
- **注意事项**：路径预览只是规划证据，不是到达证明；实际移动需单独验证。

### mcc_can_reach_position
- **用途**：检查 MCC 当前能否寻路到达世界坐标（不移动）。
- **参数**：
  - `x` (number, 必填)
  - `y` (number, 必填)
  - `z` (number, 必填)
  - `allowUnsafe` (boolean, 可选，默认 false)
  - `maxOffset` (integer, 可选，默认 0)
  - `minOffset` (integer, 可选，默认 0)
  - `timeoutMs` (integer, 可选，默认 0)
- **调用示例**：`mcc_can_reach_position(x=100, y=64, z=-200)`

### mcc_toggle_sprint
- **用途**：显式发送开始/停止疾跑的实体动作。
- **参数**：
  - `enabled` (boolean, 必填)：true=开始疾跑，false=停止
- **调用示例**：`mcc_toggle_sprint(enabled=true)`

### mcc_toggle_sneak
- **用途**：显式启用或禁用潜行。
- **参数**：
  - `enabled` (boolean, 必填)：true=潜行，false=取消潜行
- **调用示例**：`mcc_toggle_sneak(enabled=true)`

### mcc_look_at
- **用途**：将玩家视角旋转朝向世界坐标。
- **参数**：
  - `x` (number, 必填)
  - `y` (number, 必填)
  - `z` (number, 必填)
- **调用示例**：`mcc_look_at(x=100, y=64, z=-200)`
- **注意事项**：在 `mcc_raycast_block`、`mcc_use_item_on_block` 或精确方块交互前，若视角方向重要，应先使用视角工具。

### mcc_look_angles
- **用途**：将玩家视角旋转到显式 yaw 和 pitch 角度。
- **参数**：
  - `yaw` (number, 必填)：偏航角
  - `pitch` (number, 必填)：俯仰角
- **调用示例**：`mcc_look_angles(yaw=90, pitch=0)`

### mcc_look_direction
- **用途**：将玩家视角旋转到基本方向（或正上/正下）。
- **参数**：
  - `direction` (string, 必填)：方向值，如 `north` / `south` / `east` / `west` / `up` / `down`
- **调用示例**：`mcc_look_direction(direction="north")`

---

## 8. 动作交互类

### mcc_dig_block
- **用途**：在目标位置挖掘方块。
- **参数**：
  - `x` (number, 必填)
  - `y` (number, 必填)
  - `z` (number, 必填)
  - `durationSeconds` (number, 可选，默认 0)：挖掘持续秒数
- **调用示例**：`mcc_dig_block(x=100, y=64, z=-200)`
- **注意事项**：调用成功 ≠ 挖完。需重新检查目标方块或附近方块搜索结果。

### mcc_place_block
- **用途**：在目标方块位置放置当前手持的方块/物品。
- **参数**：
  - `x` (integer, 必填)
  - `y` (integer, 必填)
  - `z` (integer, 必填)
  - `face` (string, 可选，默认 "Up")：放置的面
  - `hand` (string, 可选，默认 "MainHand")：使用的手
  - `lookAtBlock` (boolean, 可选，默认 false)：是否看向目标方块
- **调用示例**：`mcc_place_block(x=100, y=63, z=-200, face="Up")`

### mcc_use_item_on_block
- **用途**：在目标方块位置使用当前手持物品。
- **参数**：
  - `x` (number, 必填)
  - `y` (number, 必填)
  - `z` (number, 必填)
- **调用示例**：`mcc_use_item_on_block(x=100, y=64, z=-200)`
- **注意事项**：使用前若视角方向重要，先用视角工具对准。

### mcc_use_item_on_hand
- **用途**：使用当前手持物品（如吃食物、喝药水、使用工具）。
- **参数**：无
- **调用示例**：`mcc_use_item_on_hand()`

### mcc_animation
- **用途**：播放指定手的手臂挥动动画。
- **参数**：
  - `hand` (string, 可选，默认 "MainHand")：`MainHand` 或 `OffHand`
- **调用示例**：`mcc_animation(hand="MainHand")`

### mcc_respawn
- **用途**：当受控玩家死亡时发送重生数据包。
- **参数**：无
- **调用示例**：`mcc_respawn()`
- **注意事项**：仅在玩家死亡状态下使用。

### mcc_send_chat
- **用途**：向连接的服务器发送聊天文本或斜杠命令。
- **参数**：
  - `text` (string, 必填)：要发送的文本
- **调用示例**：
  - `mcc_send_chat(text="hello")`
  - `mcc_send_chat(text="/tp @s 0 64 0")`
- **注意事项**：聊天/命令效果应通过状态变化、聊天历史或其他直接观察验证。

---

## 9. 事件与信息类

### mcc_recent_events
- **用途**：获取指定事件 ID 之后的高信号 MCP 运行时事件。
- **参数**：
  - `afterId` (integer, 可选，默认 0)：起始事件 ID
  - `maxCount` (integer, 可选，默认 50)：最大返回数量
  - `typeFilter` (string, 可选)：事件类型过滤
- **调用示例**：`mcc_recent_events(afterId=100, maxCount=50)`
- **注意事项**：验证会产生明确运行时事件的结果（如 `inventory_open`、`inventory_close`、`death`、`respawn`、`title`、`actionbar`）时使用。

### mcc_chat_history
- **用途**：获取 MCC 最近看到的聊天/系统行。
- **参数**：
  - `maxCount` (integer, 可选，默认 50)：最大返回数量
  - `includeJson` (boolean, 可选，默认 false)：是否包含 JSON 原始格式
- **调用示例**：`mcc_chat_history(maxCount=20)`

### mcc_agent_guidance
- **用途**：获取面向外部代理的 MCC MCP 操作员提示包（技能、系统提示、最佳实践、能力快照）。
- **参数**：无
- **调用示例**：`mcc_agent_guidance()`

### mcc_internal_commands_list
- **用途**：列出可用的 MCC 内部命令及其用法与描述。
- **参数**：无
- **调用示例**：`mcc_internal_commands_list()`

### mcc_run_internal_command
- **用途**：运行 MCC 内部命令。
- **参数**：
  - `command` (string, 必填)：不含前导斜杠的 MCC 命令行
- **调用示例**：`mcc_run_internal_command(command="health")`
- **注意事项**：仅在没有专门 MCP 工具能干净覆盖任务时使用此底层逃生口。

---

## 10. MCC 内部命令 (mcc_run_internal_command)

以下为通过 `mcc_run_internal_command(command="...")` 调用的内部命令（共 45 条，来自 `mcc_internal_commands_list`）：

| 命令 | 用法 | 说明 |
|---|---|---|
| `achievement` | `achievement <list\|locked\|unlocked>` | 列出服务器上的成就/进度 |
| `animation` | `animation <mainhand\|offhand>` | 挥动手臂 |
| `bed` | `bed leave\|sleep <x> <y> <z>\|sleep <radius>` | 右键床睡觉或离开床 |
| `blockinfo` | `blockinfo <x> <y> <z> [-s]` | 输出指定坐标的方块类型（`-s` 报告四周方块） |
| `book` | `book <read\|write\|edit\|sign>` | 读/写/编辑/签名主手上的书 |
| `bots` | `bots [list\|unload <bot name\|all>]` | 列出/加载/卸载 ChatBot |
| `changeslot` | `changeslot <1-9>` | 变更快捷栏槽位 |
| `chunk` | `chunk status [chunkX chunkZ\|locationX locationY locationZ]` | 显示区块加载状态 |
| `clear-console` | `clear-console` | 清空控制台输出 |
| `connect` | `connect <server> [account]` | 连接到指定服务器 |
| `console-chat` | `console-chat [on\|off]` | 切换控制台聊天可见性 |
| `debug` | `debug [on\|off\|state]` | 切换调试消息 |
| `dialog` | `dialog [show\|open\|set\|click\|click-label\|cancel\|dismiss]` | 查看/交互当前服务器自定义对话框 |
| `dig` | `dig <x> <y> <z>` | 尝试破坏一个方块 |
| `dropitem` | `dropitem <itemtype>` | 丢弃玩家容器/打开容器中的指定类型物品 |
| `effects` | `effects` | 列出当前激活的效果 |
| `enchant` | `enchant <top\|middle\|bottom>` | 使用已打开的附魔台附魔 |
| `entity` | `entity [near] <id\|entitytype> <attack\|use>` | 实体操作 |
| `execif` | `execif "<condition/expression>" "<command>"` | 条件成立时执行命令（支持 C# 表达式与 %变量%） |
| `execmulti` | `execmulti <cmd1> -> <cmd2> -> <cmd3> -> ...` | 依次执行多个命令 |
| `exit` | `exit` | 断开与服务器的连接 |
| `health` | `health` | 显示生命值和饱食度 |
| `inventory` | `inventory <player\|container\|<id>> <action>` | 容器相关命令 |
| `list` | `list` | 获取玩家列表 |
| `log` | `log <text>` | 将文本记录到控制台 |
| `look` | `look <x y z\|yaw pitch\|up\|down\|east\|west\|north\|south>` | 看向方向或坐标 |
| `minimap` | `minimap [on\|off]` 等 | 切换 TUI 小地图叠加层及缩放/名称/位置/洞穴模式 |
| `move` | `move <on\|off\|get\|up\|down\|east\|west\|north\|south\|center\|x y z\|gravity [on\|off]> [-f]` | 移动控制；`-f` 允许不安全移动 |
| `nameitem` | `nameitem <item name>` | 铁砧界面打开且物品在第一格时命名 |
| `recipebook` | `recipebook <list\|craft\|craftall> [recipe id]` | 列出已解锁配方并合成 |
| `reco` | `reco [account]` | 重启并重新连接服务器 |
| `reload` | `reload` | 重新加载 MCC 设置 |
| `respawn` | `respawn` | 死亡后重生 |
| `script` | `script <scriptname>` | 运行脚本文件 |
| `send` | `send <text>` | 发送聊天信息或命令 |
| `set` | `set varname=value` | 设置自定义 %variable% |
| `setrnd` | `setrnd ...` | 为自定义 %变量名% 随机赋值 |
| `sneak` | `sneak` | 切换潜行 |
| `tab` | `tab` | 显示类似原版的 Tab 列表 |
| `teams` | `teams` | 列出所有记分板队伍及其成员 |
| `tps` | `tps` | 显示服务器当前 TPS（可能不精确） |
| `tryout` | `tryout [list\|tui]` | 尝试推荐功能 |
| `upgrade` | `upgrade [-f\|check\|cancel\|download]` | 检查/下载 MCC 更新 |
| `useblock` | `useblock <x> <y> <z> [mainhand\|offhand]` | 放置方块或打开箱子 |
| `useitem` | `useitem [mainhand\|offhand] \| useitem [x] [y] [z] [mainhand\|offhand]` | 使用手中物品，可选在特定方块上使用 |

---

## 11. 通用调用约定与最佳实践

### 通用约定
- **工具命名**：所有 MCP 工具以 `mcc_` 前缀命名。
- **必填参数**：如 `itemType`、`count`、坐标 `x/y/z`、`entityId`、`playerName` 等为必填；未标必填的参数均有默认值。
- **库存 ID 约定**：`inventoryId=0` 表示玩家库存；`inventoryId=-1` 表示当前活动的非玩家容器。
- **枚举名**：物品类型使用枚举名（如 `Diamond`、`Dirt`），可通过 `mcc_materials_list`、`mcc_items_list` 查询可用名称。
- **超时参数**：`timeoutMs=0` 表示由系统决定超时，不传或传 0 均可。

### 操作循环
1. **先查询**：行动前用观察类工具检查当前情况。
2. **最短计划**：用最少的高信号工具完成动作。
3. **后验证**：动作后用新读取验证结果（fresh observation 优先于意图）。
4. **如实报告**：只报告已验证的事实，区分"已证实/推断/未知"。

### 推荐工具组合
- **移动玩家**：`mcc_player_locate` → `mcc_path_preview`/`mcc_can_reach_position` → `mcc_move_to_player` → `mcc_player_stats` 验证到达。
- **给玩家物品**：定位玩家 → 库存快照 → 移动到玩家 → `mcc_inventory_drop_item` → 新快照复核 → 可选 `mcc_items_list` 确认掉落物。注意：只能声称"丢在玩家附近"，除非观察到对方拾取。
- **容器转移**：`mcc_container_open_at` → `mcc_container_deposit_item`/`mcc_container_withdraw_item` → 复核计数 → `mcc_container_close`。
- **拾取/挖掘**：`mcc_items_list`/`mcc_blocks_find`/`mcc_raycast_block` 定位 → 需要时移动 → `mcc_items_pickup`/`mcc_dig_block` → 新查询复核。
- **事件验证**：`mcc_recent_events` 验证 `inventory_open/close`、`death`、`respawn`、`title`、`actionbar` 等事件。

### 重要禁忌
- 移动被接受 ≠ 到达；挖掘被调用 ≠ 挖完；转移被接受 ≠ 完成。一律以新观察为准。
- 不要重复发送相同的失败操作；失败后先收集一条能改变计划的新观察。
- 工具返回 `success=false`、`action_incomplete`、`capability_disabled`、`feature_disabled`、`invalid_args` 均视为失败或部分失败。
- `invalid_args` 后简化调用，最多尝试一个邻近变体，不要刷屏式猜测。
- 停止 MCC 用 `mcc_quit_client`，不要通过聊天发裸 `quit`/`exit`。
- 能力被禁用时，停止使用该类工具并说明限制。
- 报告用精确动词："移动到附近/丢弃/打开/存入/取出/拾取"；"给了/送达"必须有更强证据。
