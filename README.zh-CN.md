# MC Campus Builder

Windows 桌面工具：导入包含坐标和方块类型的 `mc-blueprint/v1` JSON/JSONL 文件，通过本机 MCC MCP Server 直接批量建造 Minecraft Java 世界。施工过程不需要 Claude 或 Claude API 参与每个方块的决策。

## 适用环境

- Windows 10/11 x64
- Leaf 1.21.11（Paper Java 兼容）
- MCC MCP Server 已启动，并已进入目标世界
- 默认 MCP endpoint：`http://127.0.0.1:33333/mcp`
- 可选 Bearer token：环境变量 `MCC_MCP_AUTH_TOKEN`

## 当前施工后端

应用按以下顺序选择后端：

1. WorldEdit：通过 `mcc_send_chat` 发送 `/pos1`、`/pos2`、`/set`，适合大范围填充；在玩家普通聊天中手动输入时通常写作 `//pos1`、`//pos2`、`//set`，但 MCC 会消费外层命令斜杠，应用不能把双斜杠原样传给该工具；
2. 原生 `/fill`：通过 `mcc_send_chat` 发送 `/fill`，适合没有 WorldEdit 的矩形填充；
3. `mcc_place_block`：逐块降级，只适合小规模显式方块，要求材料在玩家库存中。

仅发现 `mcc_send_chat` 不代表 WorldEdit 或 `/fill` 已获得权限。能力默认是“未验证”。连接后必须在界面输入三个互不重叠的测试范围/点和探针方块，点击能力验证按钮并明确确认测试世界写入；应用会分别探测 WorldEdit、原生 `/fill` 和逐块放置。只有写入返回、观察结果和 `mcc_world_block_at` 方块 ID 比较都通过的后端，才会获得带当前目标指纹和过期时间的证明；没有证明时真实施工会在确认对话框前被阻止。

## 快速开始

1. 在 Windows 安装 .NET 8 SDK，或使用发布目录中的 self-contained 程序。
2. 启动 Leaf 服务器和 MCC，并确认 MCP endpoint 可访问。
3. 设置 token（如果服务端启用认证）：

   ```powershell
   $env:MCC_MCP_AUTH_TOKEN = "你的token"
   ```

4. 启动应用，点击“连接并发现工具”。应用会执行非写入预检：会话、世界、服务器和玩家状态。
5. 在测试/备份世界输入三个互不重叠的能力探针范围/点和探针方块，点击“在指定探针坐标执行能力验证”，阅读提示后明确确认；探针本身会写入测试方块，请先准备可恢复坐标。
6. 点击“导入蓝图”，先选择 `examples/mc-blueprint.sample.json` 做测试。
7. 点击“Dry Run”，确认批次、方块数量和范围；Dry Run 不发送世界写入调用。
8. 只有所需批次类型显示为当前目标“可用”并带有效证明时，使用“开始施工（需确认）”按钮开始。
9. 出现超时或不确定结果时，应用停止并写入 journal；不要盲目重复相同 WorldEdit 命令，先采样确认。

## 蓝图格式

```json
{
  "format": "mc-blueprint/v1",
  "coordinateSystem": {
    "origin": [0, 64, 0],
    "north": "+z",
    "unit": "minecraft-block"
  },
  "bounds": { "from": [0, 64, 0], "to": [15, 70, 15] },
  "phases": [
    {
      "id": "foundation",
      "name": "地基",
      "order": 10,
      "operations": [
        {
          "id": "foundation-fill",
          "type": "fill",
          "from": [0, 64, 0],
          "to": [15, 64, 15],
          "block": "minecraft:stone"
        }
      ]
    }
  ]
}
```

也支持 JSONL，每行一个方块记录：

```json
{"phase":"details","x":4,"y":65,"z":4,"block":"minecraft:glass"}
```

蓝图必须有明确范围，方块 ID 只能使用合法的命名空间格式。应用会检查坐标、bounds、重复坐标、方块 ID、阶段和批次大小。

## 构建和测试

在 Windows 或安装了 .NET 8 SDK 的环境运行：

```powershell
dotnet restore JdMcBuilder.sln
dotnet build JdMcBuilder.sln --configuration Release
dotnet test JdMcBuilder.sln --configuration Release
dotnet publish src/JdMcBuilder.App/JdMcBuilder.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --property:PublishSingleFile=true
```

也可在 Visual Studio 2022 中打开 `JdMcBuilder.sln`，选择 `JdMcBuilder.App` 为启动项目。

## 安全边界

- 没有明确确认时不发送写入调用；
- 不把蓝图文本当作任意 shell、MCC 内部命令或服务器命令执行；
- `mcc_run_internal_command` 不是任意服务器控制台，默认不用于蓝图施工；
- 逐块后端不能假设材料无限，也不能绕过库存/视线限制；
- 目标世界的整体可用范围不取消蓝图 bounds、批次上限和阶段确认；
- 首次验收必须使用测试或备份世界，从 1×1、3×3、10×10 小范围逐步扩大。

详细工具参数和调用约束见 [`tools.md`](tools.md) 与 [`SPEC.md`](SPEC.md)。

## 当前已知限制

当前 Linux 工作区没有 .NET SDK，因此源码已写入但尚未在本环境执行 `restore/build/test/publish`；不能把当前目录称为已编译发布包。真实 MCC HTTP 报文、WorldEdit 权限、Leaf 1.21.11 服务器返回和大批量限制仍需在 Windows 测试环境中验证。校园总平面图到 JSON 蓝图的自动识别也尚未实现；目前施工输入是人工或外部工具生成的 JSON/JSONL。
