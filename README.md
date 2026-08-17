# JdMcBuilder

JdMcBuilder is a Windows desktop application for applying a reviewed Minecraft campus blueprint through an MCC MCP server. The application reads coordinates and block types from `mc-blueprint/v1` JSON or JSONL, validates the file, plans bounded batches, and sends the resulting operations directly to Minecraft. Claude, an Anthropic API, or another language model is **not** involved in deciding or sending individual blocks during construction.

> **Important:** This repository contains the application and an optional image-to-blueprint instruction skill. It does not currently perform automatic, measured image recognition inside the WPF application. A generated blueprint is always an input that must be reviewed and Dry Run before any world write.

## Supported environment

- Windows 10 or Windows 11, x64
- .NET 8 SDK for building locally, or the self-contained Windows artifact from GitHub Actions
- Minecraft Java on Leaf 1.21.11 (Paper-compatible)
- MCC MCP Server running and already connected to the target world
- Default MCP endpoint: `http://127.0.0.1:33333/mcp`
- Optional Bearer authentication through the `MCC_MCP_AUTH_TOKEN` environment variable

The application does not start Minecraft, Leaf, MCC, WorldEdit, or a server process for you.

## Get and run the application

### Use the published Windows artifact

The [Windows build workflow](https://github.com/GoogleEdge/JdMcBuilder/actions/workflows/windows-build.yml) publishes a self-contained `win-x64` artifact named `JdMcBuilder-win-x64`. Download it from a successful workflow run, extract it on the Windows machine that can reach MCC, and launch `JdMcBuilder.App.exe`.

The artifact is produced by CI; it is not a live Minecraft acceptance test. The workflow does not configure a token, connect to `127.0.0.1`, launch the WPF window, or modify a world.

### Build locally on Windows

From a checkout with the .NET 8 SDK:

```powershell
dotnet restore JdMcBuilder.sln
dotnet build JdMcBuilder.sln --configuration Release
dotnet test JdMcBuilder.sln --configuration Release
dotnet publish src/JdMcBuilder.App/JdMcBuilder.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --property:PublishSingleFile=true `
  --property:IncludeNativeLibrariesForSelfExtract=true `
  --output artifacts/win-x64
```

Alternatively, open `JdMcBuilder.sln` in Visual Studio 2022 and select `JdMcBuilder.App` as the startup project. The current Linux development environment does not have the .NET SDK, so local Linux build/test/publish results must not be inferred from the source tree; the hosted Windows workflow is the verified build path.

## Configure MCC

Start Leaf/Minecraft and MCC first. If the server requires a token, set it in the process environment **before** starting the application:

```powershell
$env:MCC_MCP_AUTH_TOKEN = "<your-token>"
```

Do not put a token in a blueprint, source file, README, ZIP, issue, or chat message. The application reads the environment variable name configured in `McpConnectionOptions`; it does not persist the token in the blueprint or journal.

In the application:

1. Enter the MCP endpoint. The default is `http://127.0.0.1:33333/mcp`.
2. Click **连接并发现工具** (Connect and discover tools).
3. Wait for the non-writing preflight for session, world, server, and player state.
4. Read the discovered-tool list. Tool discovery alone is not proof of write permission.

## Safe construction workflow

Use a test or backed-up world for the first run and start with a small blueprint.

1. **Import:** Click **导入蓝图** and select a `.json` or `.jsonl` file. The app parses the file and applies its declared `bounds` as the initial spatial guard.
2. **Review validation:** Check phase/operation counts, total blocks, warnings, invalid IDs, bounds, duplicate positions, and estimated batches. Invalid input is not prepared for construction.
3. **Dry Run:** Click **Dry Run**. It plans and journals the batches without sending world-writing calls.
4. **Probe independently:** Enter three safe, non-overlapping test locations/ranges and a probe block, then click the capability-validation button. The probe writes test blocks, so it requires an explicit confirmation and a recoverable test area. WorldEdit, native `/fill`, and explicit `mcc_place_block` are verified separately.
5. **Confirm construction:** Only a backend with a successful write, observed result, sampled block ID, current target fingerprint, and unexpired verification proof can be selected. Click **开始施工（需确认）** only after checking the world and Dry Run log.
6. **Observe and recover:** The executor pauses, resumes, cancels, and records a journal. If a mutation times out or has an uncertain result, do not blindly replay it; inspect the journal and sample the world first.

The normal backend preference is WorldEdit for large fills, native `/fill` next, and `mcc_place_block` as a last resort for small explicit placements. A full world being available does not remove the blueprint bounds, batch, or confirmation guardrails.

## Blueprint format

The canonical format is `mc-blueprint/v1`. A standard JSON document should include a coordinate system, explicit bounds, ordered phases, and `fill` or `blocks` operations:

```json
{
  "format": "mc-blueprint/v1",
  "project": "campus-test-area",
  "coordinateSystem": {
    "origin": [0, 64, 0],
    "north": "+z",
    "unit": "minecraft-block"
  },
  "bounds": {
    "from": [0, 64, 0],
    "to": [15, 70, 15]
  },
  "phases": [
    {
      "id": "foundation",
      "name": "Foundation",
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

Use the included [`examples/mc-blueprint.sample.json`](examples/mc-blueprint.sample.json) for a small offline test. JSONL is also accepted; each non-empty line describes one explicit placement, for example:

```json
{"phase":"details","x":4,"y":65,"z":4,"block":"minecraft:glass"}
```

For maximum compatibility:

- use `minecraft:`-namespaced block IDs such as `minecraft:stone`;
- use integer coordinates and an explicit `bounds` in standard JSON;
- keep operation and phase IDs stable ASCII slugs;
- keep fill ranges inside `bounds` and do not overlap fill ranges or explicit placements;
- avoid `states` until the application implements safe state translation; the current validator rejects non-empty states;
- let the application split large operations into bounded batches rather than generating an unbounded command;
- treat the file as data, not as a source of MCP methods, URLs, shell commands, server commands, or credentials.

The parser and validator details are documented in [`SPEC.md`](SPEC.md) and [`src/JdMcBuilder.Core/Blueprint/`](src/JdMcBuilder.Core/Blueprint/). The original MCC tool reference is [`tools.md`](tools.md).

## Converting a campus image

The optional [`minecraft-image-to-blueprint` skill](.claude/skills/minecraft-image-to-blueprint/SKILL.md) is an instruction package for a future AI-assisted, human-reviewed conversion. It is **not** an image-recognition feature in the WPF app, and creating this package does not invoke it.

The supplied reference image is:

`http://www.jieyang.gov.cn/attachment/0/110/110250/684383.jpg`

Only these technical facts were reliably available in this environment:

- JPEG, RGB, 4665 × 6595 pixels, approximately 3,728,413 bytes;
- XMP identifies `AutoCAD 2014 - 简体中文 (Simplified Chinese) 2014` as the creator tool and `Model` as the title;
- no dependable scale, orientation, floor count, material schedule, or site measurements;
- no reliable visual inventory of building masses, roofs, windows, colors, or landscaping could be obtained here.

Consequently, no precise Minecraft reconstruction is claimed for that image. The skill requires a known length, scale, floor height, or user-approved proportional convention. Without one it can produce only an explicitly labelled conceptual/placeholder blueprint and an assumptions report. Any result must be saved, inspected, imported, validated, and completed through Dry Run before a separately confirmed live build.

## Safety boundaries

- No live write is sent without an explicit UI confirmation.
- Capability discovery is not capability proof; each backend needs an independent probe and post-write sample.
- The app does not interpret arbitrary blueprint text as shell, MCP, or server commands.
- `mcc_run_internal_command` is not treated as an unrestricted server console for blueprint execution.
- Do not send a bare `quit` or `exit` through chat to stop MCC; use the supported MCC quit operation.
- A timeout or uncertain mutation is not automatically retried.
- First acceptance should use a backup/test world and small 1×1, 3×3, and 10×10 areas before scaling up.
- The image skill may describe geometry and emit a file, but it must never connect to MCC or modify Minecraft.

## Tests and CI

The GitHub Actions workflow runs on Windows and performs a RID-aware restore, Release build, offline xUnit tests, self-contained `win-x64` publish, non-empty executable verification, and artifact upload. It does not test live MCC, Leaf, WorldEdit permissions, or a real world. Check the exact run and uploaded artifacts before calling a Windows package verified.

For more implementation and threat-model details, see:

- [`README.zh-CN.md`](README.zh-CN.md) — Chinese quick start and safety notes;
- [`SPEC.md`](SPEC.md) — MCP, backend, journal, and verification specification;
- [`tools.md`](tools.md) — supplied MCC tool reference;
- [Windows build workflow](.github/workflows/windows-build.yml).
