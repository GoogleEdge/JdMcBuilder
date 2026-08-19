# MC Campus Builder — 产品与技术规格

> 版本：0.1（设计冻结前草案）  
> 平台：Windows 10/11 x64  
> 语言：中文  
> 状态：待用真实 MCP 工具清单和 WorldEdit 环境验收

## 1. 背景与目标

用户希望把校园总平面图中整理出的坐标和方块类型保存为文件，然后通过一个**不依赖 Claude 的 Windows 应用**直接调用已有 Minecraft MCP Server，在游戏世界中快速批量建造。

应用的职责是“导入、校验、分批、执行和恢复”，而不是在运行时分析图片或逐个询问 AI。参考图可以在应用之外被转换为蓝图；施工应用只信任导入的蓝图文件。

### 1.1 目标

- 导入含坐标和 Minecraft 方块 ID 的 JSON/JSONL 蓝图。
- 在执行前显示边界、体积、方块统计、预计调用次数和风险。
- 直接连接 MCC Team 的 MCP HTTP 服务，不经过 Claude 或 Anthropic API。
- 优先使用 WorldEdit 或 Minecraft 原生命令进行批量建造，显著减少 MCP 调用次数。
- 支持暂停、取消、断点恢复、日志和阶段性检查点。
- 对越界、错误工具、错误方块 ID、权限不足和不确定结果默认采取阻止策略。
- 允许逐步从小测试区域扩展到校园完整分区。

### 1.2 非目标（首个版本）

- 不在应用内从参考图片自动识别建筑轮廓或生成建筑设计。
- 不需要 Claude、Anthropic API Key、Claude Desktop 或任何 LLM 才能施工。
- 不尝试自动获取 Minecraft 服务器管理员权限。
- 不把任意用户输入直接拼接为任意服务器命令。
- 不保证在没有 WorldEdit、`/fill` 或批量工具时仍能高速建造；此时只能进入明确标注的慢速降级模式。
- 不在首版修改复杂方块 NBT、容器内容、命令方块或实体数据。

## 2. 参考 MCP 服务边界

实现以用户提供的文档为准：

- 文档：<https://mccteam.github.io/l10n/zh-Hans/guide/chat-bots.html#mcp-%E6%9C%8D%E5%8A%A1%E5%99%A8>
- 默认 HTTP 端点：`http://127.0.0.1:33333/mcp`
- MCC 进入 Minecraft 游戏后，内嵌 HTTP 服务才启动。
- 可选 Bearer 认证；文档建议启用 `RequireAuthToken`。
- 示例环境变量：`MCC_MCP_AUTH_TOKEN`
- 文档将 MCP 功能标为实验性；客户端必须在运行时发现实际工具和能力，不能只根据文档名称假设工具一定可用。

文档列出的、与本应用直接相关的工具包括：

- 状态/发现：`mcc_session_status`、`mcc_server_info`、`mcc_player_state`、`mcc_world_state`
- 参考/校验：`mcc_materials_list`、`mcc_block_types_list`、`mcc_world_block_at`、`mcc_block_scan`
- 执行候选：通过 `mcc_send_chat` 发送白名单 WorldEdit、`/fill` 和 `/setblock` 命令；`mcc_run_internal_command` 不作为蓝图施工入口


文档页面没有规定具体的 HTTP 方法、JSON-RPC 封装、`tools/list` 响应样例、工具参数 schema、返回体或错误码。因此本应用**不得猜测 payload**：连接后必须执行 MCP 初始化/工具发现，并以服务器返回的 schema 生成调用参数和能力报告。真实实现前必须保存一次脱敏的 `tools/list` 输出作为契约测试样本。

## 3. 用户工作流

```text
选择蓝图文件
    ↓
解析与校验（不访问世界）
    ↓
选择 MCP 连接和施工后端
    ↓
Dry Run 预览：边界、阶段、方块统计、风险、预计调用数
    ↓
用户明确确认
    ↓
连接并发现能力
    ↓
小范围测试批次（可选但默认推荐）
    ↓
按阶段批量执行
    ↓
每批写入日志/检查点
    ↓
完成后按采样坐标或扫描结果校验
```

首次连接必须先显示：

- MCC 会话是否已进入游戏；
- Minecraft 版本、维度和玩家坐标（若工具提供）；
- 发现到的工具名及 schema 摘要；
- 是否识别到 WorldEdit 执行路径；
- 是否只能使用 `/setblock` 显式方块路径。

## 4. 技术方案

### 4.1 桌面应用

首版采用 **C# / .NET 8 / WPF**：

- Windows-only，启动和后台运行开销低；
- 原生文件选择器、系统日志和打包支持成熟；
- 适合持续施工、取消和进度更新；
- 发布为 `win-x64` self-contained，用户无需单独安装 .NET Runtime。

应用不引用 Anthropic SDK。MCP 客户端、蓝图引擎和执行器全部在本地运行。

### 4.2 模块

```text
JdMcBuilder.App              WPF 界面、ViewModel、用户确认
JdMcBuilder.Core             蓝图模型、解析、校验、分批、统计
JdMcBuilder.Mcp              MCP 初始化、工具发现、调用、传输
JdMcBuilder.Backends         WorldEdit、原生命令、逐块降级后端
JdMcBuilder.Execution        队列、重试、取消、journal、恢复
JdMcBuilder.Tests             单元、契约、模拟端到端测试
```

所有长耗时操作必须异步运行，不能阻塞 UI 线程。执行器通过接口依赖 MCP，测试可以使用 fake server，不需要启动 Minecraft。

## 5. 蓝图文件规范：`mc-blueprint/v1`

### 5.1 总体要求

首版支持：

- `.json`：适合项目元数据、区域操作和中小型蓝图；
- `.jsonl`：每行一个方块，适合大文件流式导入，不要求一次性加载到内存；
- UTF-8 编码；
- 坐标为整数，方块 ID 使用命名空间格式 `minecraft:<id>`。

蓝图是数据，不是命令脚本。文件中不能包含任意 MCP method、HTTP URL、shell 命令或 WorldEdit 原始命令。

### 5.2 JSON 结构

```json
{
  "format": "mc-blueprint/v1",
  "project": "campus-graybox",
  "coordinateSystem": {
    "origin": [0, 64, 0],
    "north": "+z",
    "unit": "minecraft-block"
  },
  "bounds": {
    "min": [0, 60, 0],
    "max": [300, 120, 300]
  },
  "phases": [
    {
      "id": "site-boundary",
      "name": "校园边界",
      "order": 10,
      "operations": [
        {
          "id": "ground-001",
          "type": "fill",
          "from": [0, 64, 0],
          "to": [80, 64, 60],
          "block": "minecraft:grass_block"
        },
        {
          "id": "details-001",
          "type": "blocks",
          "blocks": [
            { "pos": [10, 65, 10], "block": "minecraft:stone" },
            { "pos": [11, 65, 10], "block": "minecraft:stone" }
          ]
        }
      ]
    }
  ]
}
```

### 5.3 JSONL 结构

每行一个独立记录，允许使用可选阶段字段：

```json
{"phase":"teaching-a","x":10,"y":65,"z":10,"block":"minecraft:stone"}
{"phase":"teaching-a","x":11,"y":65,"z":10,"block":"minecraft:stone"}
```

解析器必须逐行报告错误位置，支持取消，并在内存中只保留当前批次和必要索引。对于需要顺序施工的操作，阶段顺序由外层 manifest 或文件中明确的 `phase` 规则确定。

### 5.4 校验规则

导入时必须检查：

- `format` 是支持的版本；
- 坐标是有限整数且未超出应用配置的坐标上限；
- `min <= max`，操作范围完全位于项目 bounds 和用户 allowed region 内；
- 方块 ID 合法、非空，必要时与 `mcc_block_types_list` 的实际列表交叉校验；
- `fill` 体积不超过单次操作上限；
- 不允许重复坐标，除非显式声明覆盖策略；
- 不允许未知操作类型；
- 不包含任意命令、脚本、认证值或服务器地址；
- 汇总总方块数、按方块类型计数、按阶段计数和预计后端调用次数。

首版默认不支持 NBT。若未来加入方块状态，使用结构化字段而不是把字符串拼进命令，例如：

```json
{ "block": "minecraft:oak_log", "states": { "axis": "y" } }
```

## 6. 执行后端优先级

根据运行时发现的能力选择，顺序如下：

1. **WorldEdit 后端**：适合大体积矩形填充和重复区域，首选高速路径。
2. **Minecraft 原生命令后端**：当前通过 `mcc_send_chat` 发送白名单 `/fill` 命令；仅在已单独实现和验证其受限契约后，才可把 `mcc_run_internal_command` 作为未来候选入口。`mcc_send_chat` 返回成功和聊天/debug 中“成功填充 N 个方块”都只是发送/诊断观察，不是世界状态证明；必须对标准化范围的独立采样点使用 `mcc_world_block_at` 验证。
3. **原生 `/setblock` 后端**：仅用于小批量或稀疏显式方块；每个 placement 通过 `mcc_send_chat` 发送一条由坐标和 canonical block ID 生成的 `/setblock x y z minecraft:block`，随后用同坐标 `mcc_world_block_at` 独立验证。该后端不使用 `mcc_place_block`、库存、手持物品、移动或视线交互；界面应显示逐点命令数量和慢速提示。

原生 `/fill` 的读后验证允许在一次命令发送后进行有界、只读的 `mcc_world_block_at` 轮询，以处理 MCC/服务器缓存的短暂可见性延迟。轮询不得再次发送 `/fill`，不得根据聊天文本偏移坐标或静默切换后端；只要所有计划采样点最终没有匹配，结果就必须保持 `Unverified`/不确定。

后端选择不得静默切换。每个阶段开始前显示实际后端和预计 MCP 调用数；能力变化或调用失败时暂停并要求用户选择重试、切换或取消。`/setblock` 显式 batch 在任一 placement 发送后发生不确定错误时，整个 batch 保持不确定，不能只重放剩余点或自动切换到其他后端。

### 6.1 批次策略

- `fill` 操作优先保持为区域操作，不展开为逐方块记录。
- `blocks` 操作按相同方块 ID 和相邻坐标合并为安全的连续区域；无法安全合并的保留为有界的 `/setblock` 显式 batch。
- 默认 `maxBlocksPerOperation = 100000`，默认 `maxPayloadBytes = 512 KiB`；两者可在设置中调低或在测试世界中调高。
- WorldEdit 选区命令必须串行执行，不能并发修改同一个玩家的选区。
- 不同阶段之间默认串行；首版不为追求吞吐而并发发送会相互覆盖的操作。
- 每个操作有稳定的 `operationId` 和 `batchId`，用于 journal、日志和恢复。

## 7. WorldEdit 插件支持

### 7.1 支持目标

首版把 WorldEdit 作为一个可检测、可配置的批量后端，而不是假定所有服务器安装了相同版本。支持的核心场景：

- 长方体区域填充：选中两个角点后执行 WorldEdit 的 `set <block>`；在已验证的目标部署中，本应用通过 `mcc_send_chat` 发送 `//pos {x1},{y1},{z1} {x2},{y2},{z2}`，再发送 `//set <block>`；
- 区域替换：选区内将一种方块替换为另一种方块（对应未来的 `replace` 操作）；
- 阶段撤销：在确认版本和权限允许时使用 WorldEdit 的玩家历史撤销能力；
- 可选 schematic 工作流：生成 `.schem` 文件并由用户/服务器侧导入，再通过 WorldEdit 粘贴。

### 7.2 WorldEdit 命令适配

MCP 文档列出的 `mcc_run_internal_command` 和 `mcc_send_chat` 不是等价入口。WorldEdit 命令必须通过 `mcc_send_chat` 发送，不能改用 `mcc_run_internal_command`。对于当前已经实测成功的目标部署，`mcc_send_chat` 的 `text` 必须先使用一条 `//pos` 命令，后跟两个逗号分隔的方块向量，例如 `//pos 1,64,2 3,65,4`，再发送 `//set <block>`。当前服务器接受合并的 `//pos [pos1] [pos2...]` 形式，而不是分别发送 `//pos1`、`//pos2`。该映射是目标环境 profile，不能推导为所有 MCC、Leaf、Paper 或 WorldEdit 版本的通用规则；应用仍须在目标服务器上逐项验证实际版本和权限：

```json
{
  "selection": "//pos {x1},{y1},{z1} {x2},{y2},{z2}",
  "set": "//set {block}",
  "replace": "//replace {from} {to}",
  "undo": "//undo"
}
```

这里的双斜杠是 **当前 MCC `mcc_send_chat` 部署 profile 的输入形式**。WorldEdit 命令只通过 `mcc_send_chat` 发送；本应用不把它改走 `mcc_run_internal_command`。当前 `//pos` 命令要求两个逗号分隔的方块向量，错误提示明确显示其形式为 `//pos [pos1] [pos2...]`。该行为是目标环境的实测适配，不代表所有 MCC、Leaf、Paper 或 WorldEdit 版本。上述模板仍不代表所有环境的权限或最终返回格式。适配器必须：

1. 对坐标和 block ID 做严格参数化，不允许蓝图注入额外命令片段；
2. 先用一条 `//pos` 清晰设置两个角点，再发 `//set`/`//replace`，避免使用玩家当前残留选区；
3. 每个 WorldEdit 操作保存完整选区、方块、命令模板和 MCP 返回摘要；
4. 任何一个选区命令失败，立即停止该操作，不执行 set；
5. WorldEdit 返回不确定或超时后，不盲目重复 set，先采样检查区域或要求用户确认；
6. 如果插件拒绝权限、未安装或命令入口不可用，报告原因并按后端优先级降级。

### 7.3 WorldEdit 权限和环境前置条件

真实施工前必须确认：

- Minecraft Java/Bedrock 版本；
- WorldEdit 或兼容插件名称和版本；
- 玩家是否有选区、编辑、撤销和 schematic 权限；
- `mcc_run_internal_command` 的准确 schema（仅用于诊断/受限内部命令，不作为 WorldEdit 施工入口）；
- `mcc_send_chat` 是否能发送 WorldEdit 命令并返回成功/失败；在当前已验证目标部署中，应用传 `//pos x1,y1,z1 x2,y2,z2`，再传 `//set`；该形式是部署 profile，不要未经测试推广到其他环境；
- 服务器是否有 WorldEdit 操作的方块数量限制、异步队列或冷却；
- 是否允许从客户端/本机使用这些命令。

应用的连接页显示 WorldEdit 为 `可用`、`未确认`、`不可用` 三种状态，不能把“检测到字符串”当作已获得权限。源码中的 `worldedit`、`native-fill` 和 `native-setblock` 三个状态彼此独立；每个后端的 `可用` 状态必须同时携带目标绑定的能力验证证明（后端 ID、目标指纹、验证时间和过期时间）；仅凭工具名只能是 `未确认`；缺少写入后观察工具时为 `不可用`。WorldEdit、原生 `/fill` 和原生 `/setblock` 必须分别完成与自身后端对应的测试，不能用一个命令后端的证明授权另一个后端。在证明尚未由测试世界探测生成前，真实施工按钮必须在确认对话框之前明确阻止，不得提供启用未验证后端的生产开关。

### 7.4 Schematic 支持边界

首版可以实现**导出** `.schem` 的离线功能，但不假定应用能把文件直接上传到服务器。若 MCP 没有文件上传能力，用户需要把生成文件复制到服务器的 WorldEdit schematics 目录，之后由 WorldEdit 执行加载/粘贴；这不是自动直连施工路径。

因此：

- 大型矩形蓝图首选在线 WorldEdit command backend；
- schematic 是可选导入/导出能力；
- 不将本地文件路径直接发送给 Minecraft 服务器；
- 若未来 MCP 增加受控文件上传工具，再单独增加安全的上传能力和权限确认。

## 8. MCP 连接协议与配置

### 8.1 用户配置示例

```toml
[mcp]
transport = "http"
url = "http://127.0.0.1:33333/mcp"
authTokenEnvironmentVariable = "MCC_MCP_AUTH_TOKEN"
requestTimeoutSeconds = 30

[build]
allowedMin = [0, 0, 0]
allowedMax = [300, 200, 300]
maxBlocksPerOperation = 100000
maxPayloadBytes = 524288
requireDryRun = true
requireConfirmationForLargePhase = true

[worldedit]
enabled = true
commandTransport = "auto"
profile = "configurable"
```

应用不得把 token 明文写入日志、蓝图或异常报告。连接页允许用户只选择环境变量名，不要求把 token 粘贴到蓝图中。

### 8.2 连接流程

1. 读取本地设置和环境变量；
2. 连接指定 HTTP MCP endpoint；
3. 执行 MCP 标准初始化流程；
4. 调用工具发现，缓存带版本/时间戳的工具清单；
5. 对每个候选工具保存 schema 摘要；
6. 调用状态查询确认 MCC 已进入游戏；
7. 通过实际 schema 生成一次非破坏性能力探测报告；
8. 只有用户确认后才执行施工调用。

能力发现和施工调用分离。`tools/list`、状态查询和方块查询可能可用，不代表写入工具有权限。

当前实现的能力探测契约：能力探针必须由用户在测试/备份世界中明确确认，并为 WorldEdit、原生 `/fill`、原生 `/setblock` 分别提供互不重叠的测试范围/点和探针方块。探测先调用 `mcc_session_status`、`mcc_world_state`（可选 `mcc_server_info`）生成目标指纹，再按后端独立执行写入，并以 `mcc_world_block_at` 的新鲜方块 ID 采样作为权威验证。原生 `/fill` 可在一次命令发送后对标准化范围的去重角点进行有界只读轮询，以处理短暂可见性延迟；`mcc_chat_history` 若存在只能作为可选诊断，不能证明当前命令或目标坐标，聊天/debug 中“成功填充 N 个方块”同样不能证明世界状态。原生 `/setblock` 对每个显式 placement 只发送一次 `/setblock`，并读取同一坐标；服务器返回的“更改了位于...的方块”等文本仅作诊断，不能替代采样。只有该后端自己的写入和全部独立采样均成功时，才生成带后端 ID、目标指纹、验证时间和过期时间的 `BackendVerification`；任何权限错误、取消、超时、结果缺失、错误返回坐标或采样不匹配都不生成证明。探测失败且写入可能已发送时必须提示人工检查探针位置，不得自动重放 `/fill`、`/setblock`，偏移坐标或静默切换后端。目标指纹不匹配或过期时，真实施工 gate 继续保持关闭。

## 9. 安全与恢复

### 9.1 强制保护

- 蓝图范围必须完全位于用户配置的 allowed region；
- 首次运行默认 Dry Run；
- 大于默认方块阈值的阶段需要二次确认；
- 默认禁止清空区域、任意命令、任意文件上传和跨世界操作；
- 只允许白名单后端和白名单命令模板；
- 每批发送前记录范围、体积、方块 ID 和目标维度；
- 连接断开、权限失败、schema 改变或结果不确定时自动暂停；
- 日志中屏蔽 Bearer token、环境变量值和完整认证头。

### 9.2 Journal 与断点

每次施工创建一个 session journal，至少记录：

```json
{
  "sessionId": "2026-08-14T...-campus",
  "blueprintHash": "sha256:...",
  "world": { "dimension": "minecraft:overworld" },
  "backend": "worldedit-command",
  "completed": ["site-boundary/ground-001/batch-0001"],
  "uncertain": [],
  "lastError": null
}
```

重启后只允许从成功批次继续。对于经 `mcc_send_chat` 发送的 WorldEdit `//set`、`/fill` 或 `/setblock`，如果返回结果不确定，整个批次进入 `uncertain`，必须先用 `mcc_world_block_at`/扫描工具抽样或让用户重新确认，而不是自动重放；`/setblock` batch 不得只重放尚未发送的剩余 placement。

WorldEdit `//undo` 是当前目标部署 profile 中的可选恢复动作；若未来经 `mcc_send_chat` 实现，应按当前目标验证。无论采用何种入口，都必须确认同一玩家历史未被其他操作污染，且插件返回成功；应用不能宣称它拥有通用事务回滚。

## 10. 界面需求

### 10.1 连接页

- endpoint、认证环境变量名、超时；
- 连接/断开；
- MCC 会话、维度、玩家位置；
- 工具清单、schema 摘要、读/写状态；
- WorldEdit、原生 `/fill`、原生 `/setblock` 三个独立能力状态和测试按钮；测试按钮必须显示探针坐标、探针方块和明确写入确认。
- 能力探测目标指纹、验证时间、过期时间和失败/不确定原因；未生成当前目标绑定证明时，真实施工按钮保持禁用语义。

### 10.2 导入与预览页

- JSON/JSONL 文件选择；
- 错误行号和字段定位；
- 项目 bounds、allowed region、越界列表；
- 方块类型/阶段统计；
- 预计方块数、批次数、MCP 调用次数和后端；
- Dry Run 日志；
- “开始施工”必须是明确按钮，不因导入文件而自动写入世界。

### 10.3 施工页

- 当前阶段和 batch ID；
- 完成/失败/不确定的批次数；
- 方块数、速度、耗时、估计剩余时间；
- 暂停、取消、从检查点恢复；
- 最近错误及建议动作；
- 当前后端：WorldEdit / 原生命令 / 逐块慢速。

## 11. 性能要求

以 WorldEdit 或原生命令为主要路径：

- 大型 `fill` 不得展开为单方块 MCP 调用；
- UI 在执行 100 万方块级蓝图时保持可操作；
- JSONL 导入采用流式解析，内存占用不随总方块数线性增长（索引和当前批次除外）；
- MCP 请求有界并发，WorldEdit 选区操作默认串行；
- 每批记录延迟、方块数和吞吐量；
- 连接/工具调用超时可取消，重试不能造成无限循环。

具体吞吐量取决于 MCC、Minecraft 服务器、WorldEdit 版本、权限和服务器限制，验收时以测试世界实测为准，不在规格中承诺固定 blocks/s。

## 12. 测试与验收

### 12.1 自动化测试

- 蓝图 JSON/JSONL 解析、错误行定位和流式取消；
- 方块 ID、重复坐标、bounds、allowed region 和体积限制；
- fill/blocks 批次规划和统计；
- MCP 初始化、工具发现、参数 schema 校验、超时和取消；
- fake MCP server 下的成功、拒绝、断连、重试和不确定结果；
- WorldEdit 命令模板参数化，禁止命令注入；
- journal 写入、崩溃恢复和稳定 operation ID；
- Dry Run 不调用任何写入工具；
- token 不出现在日志、错误和导出报告。

### 12.2 真实端到端验收

按以下顺序进行，禁止首次就在主世界执行：

1. MCC 本地测试世界连接和工具发现；
2. 1×1 或 3×3 蓝图的逐块测试；
3. 10×10 `fill` 测试；
4. 通过 `mcc_send_chat` 进行 WorldEdit `//pos x1,y1,z1 x2,y2,z2` + `//set` 测试；
5. 故意断开连接，验证断点恢复；
6. 越界蓝图，确认被阻止；
7. 一个教学楼/运动场分区；
8. 采样查询或截图确认结果，再扩展到完整校园。

验收通过条件：没有未确认的越界写入；执行阶段、批次、方块统计和错误可追溯；WorldEdit 不可用时应用明确降级而不是假装成功；大区域不因逐块调用导致不可接受的调用数量。

## 13. 交付阶段

1. **规格和契约**：冻结本文件，收集真实 `tools/list` 脱敏样本和 WorldEdit 版本/权限信息。
2. **核心引擎**：实现蓝图模型、解析器、校验器、统计和批次规划。
3. **MCP 客户端**：实现 HTTP 连接、初始化、发现、调用、错误分类和 fake server。
4. **WorldEdit 后端**：实现可配置 command profile、串行选区/填充、结果确认和安全限制。
5. **降级后端**：实现原生命令候选路径和逐块路径。
6. **WPF 界面**：连接、导入、预览、施工、journal 和日志。
7. **测试世界验收**：按第 12 节逐级扩大。
8. **Windows 发布**：`win-x64` self-contained 安装包和诊断版。

## 14. 实现前必须取得的信息

以下信息缺失时只能实现 fake MCP 和离线蓝图功能，不能安全完成真实施工适配：

1. MCP Server 的实际 endpoint、是否启用 Bearer token；
2. 一次真实、脱敏的 MCP `initialize`/工具发现结果；
3. `mcc_run_internal_command` 的完整 input schema，以及它是否能执行 `/fill`、WorldEdit 命令；
4. `mcc_send_chat` 的完整 input schema，以及发送 WorldEdit 命令（当前目标部署应用传 `//pos x1,y1,z1 x2,y2,z2` 和 `//set`）时的返回格式；
5. `mcc_send_chat` 发送 `/setblock` 的完整 input schema 和失败返回；
6. Minecraft 版本、Java/Bedrock、目标维度和玩家权限；
7. WorldEdit/兼容插件版本、权限节点、单次操作限制和是否允许当前 profile 的 `//undo`；
8. 测试世界坐标范围和允许施工区域。

在上述信息确认前，任何“已经支持真实 WorldEdit 建造”的说法都不成立；实现必须在 UI 中显示“未验证”状态。

---

# 附录 A：MCC MCP 工具使用速查（来源：tools.md）

本附录把当前 `tools.md` 的工具名、参数和调用示例纳入产品规格。应用通过 MCP 的 `tools/call` 能力调用工具；下表的示例是工具参数对象的表达，不是要求用户手工拼接 HTTP/JSON-RPC。实际连接后仍须以服务器返回的工具 schema 为准。参数中的 `?` 表示可选，`=` 后为文档默认值。所有写入工具只在执行器的白名单和用户确认后开放。

## A.1 会话、连接与服务器

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_session_status` | 无 | `{}` | 首个预检；确认连接、能力和位置；默认启用 |
| `mcc_server_info` | 无 | `{}` | 获取服务器连接信息/TPS；用于限速 |
| `mcc_disconnect` | 无 | `{}` | 断开服务器但不退出 MCC；仅用户明确请求时 |
| `mcc_quit_client` | 无 | `{}` | 干净退出 MCC；应用停止 MCC 时唯一推荐工具 |
| `mcc_loaded_bots` | 无 | `{}` | 诊断加载的 bot/script；只读 |

## A.2 世界、区块与方块

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_world_state` | 无 | `{}` | 预检维度、区块加载、时间/天气 |
| `mcc_chunk_status` | `x?` `y?` `z?` number | `{"x":100,"y":64,"z":-200}` | 施工区域加载检查 |
| `mcc_world_block_at` | `x,y,z` integer 必填 | `{"x":100,"y":64,"z":-200}` | 单点施工后验证 |
| `mcc_block_types_list` | `filter?` string, `maxCount?` integer=500 | `{"filter":"stone","maxCount":50}` | 校验服务端已知 block 类型 |
| `mcc_blocks_find` | `query?`, `radius?`=6, `maxCount?`=200, `exactMatch?`=false | `{"query":"oak_log","radius":8,"exactMatch":true}` | 附近方块查找 |
| `mcc_block_scan` | `radius?`=3, `maxCount?`=200, `materialFilter?` | `{"radius":5,"materialFilter":"stone"}` | 小范围采样/验证，不替代大批量扫描 |
| `mcc_raycast_block` | `maxDistance?`=8 number, `includeNeighbors?`=false | `{"maxDistance":8}` | 逐块交互时确定视线命中 |
| `mcc_materials_list` | `filter?`, `maxCount?`=500 | `{"filter":"diamond"}` | 查询材料/物品名 |
| `mcc_signs_find` | `text` 必填, `exactMatch?`=false, `radius?`=16, `maxCount?`=50, `includeBackText?`=true | `{"text":"shop","radius":32}` | 查找告示牌；与建造无关的只读工具 |

## A.3 玩家状态与在线玩家

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_player_state` | 无 | `{}` | 读取玩家状态 |
| `mcc_player_stats` | 无 | `{}` | 通用 MCC 玩家状态参考；JdMcBuilder 的显式 `/setblock` 后端不使用 |
| `mcc_status_effects` | 无 | `{}` | 读取状态效果 |
| `mcc_players_list` | 无 | `{}` | 在线玩家列表 |
| `mcc_players_detailed` | `includeSelf?`=false, `includeCoordinates?`=true | `{"includeSelf":true}` | 玩家详情/位置 |
| `mcc_player_locate` | `playerName` 必填, `includeSelf?`=false | `{"playerName":"Builder"}` | 定位玩家 |
| `mcc_player_nearby` | `playerName?`, `radius?`=32, `includeSelf?`=false | `{"radius":16}` | 检查施工区附近玩家；碰撞安全提示 |

## A.4 实体

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_entities_list` | `maxCount?`=100, `typeFilter?`, `radius?`=0 | `{"typeFilter":"zombie","radius":32}` | 实体列表 |
| `mcc_entities_query` | `maxCount?`=50 | `{"maxCount":50}` | 跟踪实体查询 |
| `mcc_entity_types_list` | `filter?`, `maxCount?`=500 | `{"filter":"cow"}` | 实体类型列表 |
| `mcc_entity_nearest` | `typeFilter?`, `nameFilter?`, `radius?`=64, `includePlayers?`=true | `{"typeFilter":"creeper","radius":32}` | 最近实体 |
| `mcc_entity_info` | `entityId` 必填, `includeMetadata?`=false, `includeEquipment?`=true, `includeEffects?`=true | `{"entityId":123}` | 实体详情 |
| `mcc_entity_interact` | `entityId` 必填, `interaction?`="Interact", `hand?`="MainHand" | `{"entityId":123}` | 实体交互；首版不用于建造 |
| `mcc_entity_attack` | `entityId` 必填 | `{"entityId":123}` | 攻击；默认禁用 |
| `mcc_items_list` | `itemType?`, `radius?`=32, `maxCount?`=100 | `{"itemType":"Diamond","radius":16}` | 掉落物列表 |

## A.5 物品、库存与容器

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_items_pickup` | `itemType` 必填, `radius?`=32, `maxItems?`=20, `allowUnsafe?`=false, `timeoutMs?`=0 | `{"itemType":"Apple","maxItems":10}` | 拾取；首版不自动调用 |
| `mcc_inventory_snapshot` | `inventoryId?`=0 | `{"inventoryId":0}` | 玩家/容器库存快照 |
| `mcc_inventory_search` | `query` 必填, `maxCount?`=100, `exactMatch?`=false, `includeContainers?`=true | `{"query":"stone"}` | 检查材料库存 |
| `mcc_inventory_drop_item` | `itemType,count` 必填, `inventoryId?`=0, `preferStack?`=false | `{"itemType":"Dirt","count":1}` | 丢弃；首版默认禁用 |
| `mcc_inventories_list` | 无 | `{}` | 当前库存/容器列表 |
| `mcc_select_item` | `itemType` 必填, `preferLowestSlot?`=true | `{"itemType":"Stone"}` | 通用 MCC 玩家交互参考；JdMcBuilder 的显式 `/setblock` 后端不使用 |
| `mcc_change_hotbar_slot` | `slot` 必填，1-9 | `{"slot":1}` | 通用 MCC 玩家交互参考；JdMcBuilder 的显式 `/setblock` 后端不使用 |
| `mcc_container_open_at` | `x,y,z` integer 必填, `timeoutMs?`=0, `closeCurrent?`=true | `{"x":11000,"y":64,"z":11021}` | 打开容器 |
| `mcc_container_close` | `inventoryId?`=-1, `timeoutMs?`=0 | `{"inventoryId":-1}` | 关闭容器 |
| `mcc_container_deposit_item` | `itemType,count` 必填, `inventoryId?`=-1, `preferLargestStack?`=true | `{"itemType":"Diamond","count":5}` | 存入容器 |
| `mcc_container_withdraw_item` | `itemType,count` 必填, `inventoryId?`=-1, `preferLargestStack?`=true | `{"itemType":"Diamond","count":3}` | 取出容器物品 |
| `mcc_inventory_window_action` | `inventoryId,slotId,actionType` 必填 | `{"inventoryId":0,"slotId":5,"actionType":"LeftClick"}` | 底层库存操作；默认不使用 |

## A.6 移动、路径与视角

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_move_to` | `x,y,z` number 必填；`allowUnsafe?`=false；`allowDirectTeleport?`=false；`maxOffset?`=0；`minOffset?`=0；`timeoutMs?`=0 | `{"x":100,"y":64,"z":-200,"maxOffset":1}` | 逐块交互移动；必须后验 |
| `mcc_move_to_player` | `playerName` 必填；安全/偏移/超时同上 | `{"playerName":"Builder","maxOffset":2}` | 移动到玩家 |
| `mcc_path_preview` | `x,y,z` 必填；`allowUnsafe?`,`maxOffset?`,`minOffset?`,`timeoutMs?`,`maxWaypoints?`=128 | `{"x":100,"y":64,"z":-200}` | 不实际移动的路径预览 |
| `mcc_can_reach_position` | `x,y,z` 必填；`allowUnsafe?`,`maxOffset?`,`minOffset?`,`timeoutMs?` | `{"x":100,"y":64,"z":-200}` | 可达性检查 |
| `mcc_toggle_sprint` | `enabled` 必填 | `{"enabled":true}` | 移动控制 |
| `mcc_toggle_sneak` | `enabled` 必填 | `{"enabled":true}` | 移动控制 |
| `mcc_look_at` | `x,y,z` number 必填 | `{"x":100,"y":64,"z":-200}` | 逐块前对准方块 |
| `mcc_look_angles` | `yaw,pitch` number 必填 | `{"yaw":90,"pitch":0}` | 显式视角 |
| `mcc_look_direction` | `direction` 必填：north/south/east/west/up/down | `{"direction":"north"}` | 基本方向 |

## A.7 方块动作、聊天与命令

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_dig_block` | `x,y,z` number 必填, `durationSeconds?`=0 | `{"x":100,"y":64,"z":-200}` | 挖掘；默认禁用，避免破坏 |
| `mcc_place_block` | `x,y,z` integer 必填, `face?`="Up", `hand?`="MainHand", `lookAtBlock?`=false | `{"x":100,"y":64,"z":-200,"face":"Up","lookAtBlock":true}` | 通用 MCC 玩家交互参考；JdMcBuilder 的显式 `/setblock` 后端不使用 |
| `mcc_use_item_on_block` | `x,y,z` number 必填 | `{"x":100,"y":64,"z":-200}` | 使用手中物品 |
| `mcc_use_item_on_hand` | 无 | `{}` | 使用手中物品 |
| `mcc_animation` | `hand?`="MainHand" | `{"hand":"MainHand"}` | 手臂动画 |
| `mcc_respawn` | 无 | `{}` | 死亡后重生 |
| `mcc_send_chat` | `text` string 必填 | `{"text":"/fill 0 64 0 10 64 10 minecraft:stone"}` | WorldEdit/原生命令候选写入入口；必须使用生成的白名单命令。当前目标部署的 WorldEdit 输入为 `//pos x1,y1,z1 x2,y2,z2` 和 `//set`；WorldEdit 命令不通过 `mcc_run_internal_command` 发送 |

## A.8 事件、聊天与 MCC 信息

| 工具 | 参数 | 参数示例 | 用途/策略 |
|---|---|---|---|
| `mcc_recent_events` | `afterId?`=0, `maxCount?`=50, `typeFilter?` | `{"afterId":100,"maxCount":50}` | 读取高信号结果事件 |
| `mcc_chat_history` | `maxCount?`=50, `includeJson?`=false | `{"maxCount":20,"includeJson":true}` | 验证命令/服务器反馈 |
| `mcc_agent_guidance` | 无 | `{}` | 获取 MCC 操作提示/能力快照 |
| `mcc_internal_commands_list` | 无 | `{}` | 获取内部命令目录；启动或诊断时调用 |

## A.9 `mcc_run_internal_command`

参数为 `command`（string 必填），例如：`{"command":"health"}`。这是 MCC 内部命令，不等同于任意 Minecraft 控制台。默认只读/诊断白名单为 `health`、`blockinfo <x> <y> <z> [-s]`、`chunk status ...`、`tps`、`list`、`look ...`、`move ...`；兼容发送命令的 `send <text>` 必须经过模板白名单和权限测试。默认禁止蓝图驱动 `execif`、`execmulti`、`script`、`connect`、`reco`、`reload`、`exit`、`clear-console` 等。

完整内部命令索引（具体用法以 `mcc_internal_commands_list()` 为运行时准则）：

| 命令 | 用法 |
|---|---|
| `achievement` | `achievement <list\|locked\|unlocked>` |
| `animation` | `animation <mainhand\|offhand>` |
| `bed` | `bed leave\|sleep <x> <y> <z>\|sleep <radius>` |
| `blockinfo` | `blockinfo <x> <y> <z> [-s]` |
| `book` | `book <read\|write\|edit\|sign>` |
| `bots` | `bots [list\|unload <bot name\|all>]` |
| `changeslot` | `changeslot <1-9>` |
| `chunk` | `chunk status [chunkX chunkZ\|locationX locationY locationZ]` |
| `clear-console` | `clear-console` |
| `connect` | `connect <server> [account]` |
| `console-chat` | `console-chat [on\|off]` |
| `debug` | `debug [on\|off\|state]` |
| `dialog` | `dialog [show\|open\|set\|click\|click-label\|cancel\|dismiss]` |
| `dig` | `dig <x> <y> <z>` |
| `dropitem` | `dropitem <itemtype>` |
| `effects` | `effects` |
| `enchant` | `enchant <top\|middle\|bottom>` |
| `entity` | `entity [near] <id\|entitytype> <attack\|use>` |
| `execif` | `execif "<condition/expression>" "<command>"` |
| `execmulti` | `execmulti <cmd1> -> <cmd2> -> <cmd3> -> ...` |
| `exit` | `exit` |
| `health` | `health` |
| `inventory` | `inventory <player\|container\|<id>> <action>` |
| `list` | `list` |
| `log` | `log <text>` |
| `look` | `look <x y z\|yaw pitch\|up\|down\|east\|west\|north\|south>` |
| `minimap` | `minimap [on\|off]` |
| `move` | `move <on\|off\|get\|up\|down\|east\|west\|north\|south\|center\|x y z\|gravity [on\|off]] [-f]` |
| `nameitem` | `nameitem <item name>` |
| `recipebook` | `recipebook <list\|craft\|craftall> [recipe id]` |
| `reco` | `reco [account]` |
| `reload` | `reload` |
| `respawn` | `respawn` |
| `script` | `script <scriptname>` |
| `send` | `send <text>` |
| `set` | `set varname=value` |
| `setrnd` | `setrnd ...` |
| `sneak` | `sneak` |
| `tab` | `tab` |
| `teams` | `teams` |
| `tps` | `tps` |
| `tryout` | `tryout [list\|tui]` |
| `upgrade` | `upgrade [-f\|check\|cancel\|download]` |
| `useblock` | `useblock <x> <y> <z> [mainhand\|offhand]` |
| `useitem` | `useitem [mainhand\|offhand] \| useitem [x] [y] [z] [mainhand\|offhand]` |

## A.10 建造调用模板与验证

### WorldEdit（首选，需权限测试）

```text
mcc_send_chat({"text":"//pos x1,y1,z1 x2,y2,z2"})
mcc_send_chat({"text":"//set minecraft:stone"})
mcc_world_block_at({"x":x1,"y":y1,"z":z1})
```

`mcc_world_block_at` 的返回方块 ID 必须与本批次期望的 block ID 比较；当前 MCC 返回对象中的文本 `material`（例如 `Stone`）会被规范化为 `minecraft:stone` 后比较，数值 `blockId`/`blockMeta` 单独不能安全推断 canonical 方块 ID。仅确认读取工具调用成功不算施工验证。验证失败或无法解析方块 ID 时，批次必须标记为不确定并暂停。

### Leaf/Paper 原生命令（次选，需权限测试）

```text
mcc_send_chat({"text":"/fill x1 y1 z1 x2 y2 z2 minecraft:stone"})
mcc_world_block_at({"x":x1,"y":y1,"z":z1})
```

原生命令同样必须解析并比较返回的方块 ID；HTTP/MCP 返回成功但方块值不匹配时，不得报告批次成功。实现会从标准化的 `Min`/`Max` 组合生成最多八个去重角点，并在一次 `/fill` 发送后做有限只读轮询。初始 `air` 可以作为缓存/传播延迟观察继续轮询，但不会再次发送命令；轮询耗尽、部分角点不匹配、返回坐标错误、无法解析、取消、超时或传输失败都必须报告不确定。聊天历史和 Minecraft chat/debug 的成功文本只能记录为诊断，不能单独生成能力证明。

### 原生 `/setblock` 显式后端（仅小批量）

```text
mcc_send_chat({"text":"/setblock x y z minecraft:stone"})
mcc_world_block_at({"x":x,"y":y,"z":z})
```

应用按确定的 placement 顺序为每个方块生成并发送一条经过坐标和 block ID 校验的 `/setblock`，只使用 `mcc_send_chat`；不调用 `mcc_place_block`、`mcc_select_item`、`mcc_player_stats`，不要求库存、手持物品、移动或视线。`mcc_send_chat` 返回的系统文本（例如“更改了位于6, 64, 1的方块”）仅作诊断。每个点必须用同坐标的 `mcc_world_block_at` 读取并比较 canonical 方块 ID；返回坐标缺失/错误、无法解析、方块不匹配、取消、超时或传输失败都使整个显式 batch 保持不确定，禁止自动重放或改用旧的物理放置后端。

能力探测和真实施工使用相同的保守不确定性规则：写入调用已经可能发出后，MCP 传输异常、取消、超时、验证回调异常、非法结果或 journal 保存失败都不得作为确定失败重试；必须写入 `uncertain` journal 并等待人工/新鲜采样确认。HTTP JSON-RPC 响应还必须是 `jsonrpc=2.0` 且 response `id` 与请求 ID 一致，缺失或错配按协议失败处理。

所有模板都必须先做 Dry Run、检查 bounds/allowed region、确认目标维度，再执行。WorldEdit 或 `/fill` 返回超时/不确定时暂停，不自动重放；工具结果中的 `success=false`、`action_incomplete`、`capability_disabled`、`feature_disabled`、`invalid_args` 均记为失败或部分失败。停止 MCC 使用 `mcc_quit_client`，不得向聊天发送裸 `quit`/`exit`。
