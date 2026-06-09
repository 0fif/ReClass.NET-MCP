# ReClass.NET MCP - _Enhanced_

This fork provides an MCP (Model Context Protocol) integration for ReClass.NET,
allowing Claude Code, Codex, opencode, and other MCP clients to interact with
ReClass.NET for memory analysis and reverse engineering tasks.

## Why This Fork

This fork is meant to be the reproducible, compatibility-focused version of the
original ReClass.NET MCP integration.

Compared with the original project, this fork adds:

- A pinned ReClass.NET source commit in `RECLASSNET_COMMIT`
- Build checks so the plugin is compiled against the ReClass.NET source it is meant to run with
- Compatibility handling for `ClassNode.Uuid` API differences between older release builds and newer source builds
- Exact-offset field insertion with automatic padding/splitting
- Nested class/structure insertion for real struct composition
- Safer pointer handling so plain `pointer` fields do not create throwaway dummy classes
- Paged and filtered `GetNodes` output so large sparse classes do not force multi-megabyte MCP responses
- Short-lived TCP connections so MCP calls keep working after ReClass.NET closes idle sockets
- Updated MCP server docs and `uv` dependency locking

## Components

### 1. ReClassMCP.Plugin (C#)

A ReClass.NET plugin that:
- Starts a TCP server on port 27015 when ReClass.NET loads
- Exposes ReClass.NET functionality via JSON-RPC over TCP
- Handles memory reading/writing, class/node management, and process inspection

### 2. ReClassMCP.Server (Python)

An MCP server that:
- Connects to the ReClass.NET plugin via TCP
- Translates MCP protocol to plugin commands
- Exposes tools for MCP clients to use

## Compatibility

The C# plugin must be built against the same ReClass.NET code that you run.

Downloading the official ReClass.NET `v1.2` release is not the same as cloning
ReClass.NET and building current `master`. The release download is old tagged code
from 2019. Current `master` is newer unreleased source code.

Do not build the plugin against current `master` and then load it into the old
official `v1.2` release download. Build and run them from the same ReClass.NET checkout.

ReClass.NET may still report assembly version `1.2.0.0` across incompatible
source snapshots, so the assembly version alone is not a compatibility check.

There is no newer official release number than `v1.2` right now, but there is
newer source code. This matters because the official `v1.2` release and current source
can both identify as `1.2.0.0` while exposing different APIs:

```text
official v1.2 release -> ClassNode.Uuid is NodeUuid
current master source -> ClassNode.Uuid is Guid
```

This fork pins the intended ReClass.NET source commit in `RECLASSNET_COMMIT`.
The build script checks that `ReClass.NET/` is present and checked out at that
commit before compiling.

The plugin also avoids direct compile-time UUID calls so class lookup works with
both known UUID shapes:

- older v1.2 release builds: `ClassNode.Uuid : NodeUuid`
- newer source builds: `ClassNode.Uuid : Guid`

## Installation

### Getting the Plugin

**Option 1: Download Pre-built Release**

1. Download `ReClassMCP.zip` from the [Releases](../../releases) page
2. Use it only with the ReClass.NET build named in that release
3. Extract and copy the contents of the `x64/` or `x86/` folder to that ReClass.NET `Plugins` folder
4. The `MCP-Server/` folder contains the Python MCP server

**Option 2: Build from Source**

1. Clone this repo and the pinned ReClass.NET source:
   ```powershell
   git clone https://github.com/0fif/ReClass.NET-MCP.git
   cd ReClass.NET-MCP
   git clone https://github.com/ReClassNET/ReClass.NET.git
   git -C ReClass.NET checkout (Get-Content .\RECLASSNET_COMMIT)
   ```
2. Close ReClass.NET if it is already running from this checkout
3. Open the solution in Visual Studio 2019+ or run `.\build.ps1`
4. Run the ReClass.NET executable from the built `ReClass.NET` tree
5. Copy `ReClassMCP.dll` and `Newtonsoft.Json.dll` from the build output to that same ReClass.NET `Plugins` folder

### Installing the MCP Server

1. Install Python 3.10 or later
2. Install the MCP server dependencies:

```bash
cd ReClassMCP.Server
pip install -r requirements.txt
```

### Configuring an MCP Client

Add the following command/args to your MCP client configuration. For Claude Code,
this goes in `~/.claude/mcp.json` or project `.claude/mcp.json`:

```json
{
  "mcpServers": {
    "reclass": {
      "command": "python",
      "args": ["/path/to/ReClass.NET-MCP/ReClassMCP.Server/reclass_mcp_server.py"],
      "env": {}
    }
  }
}
```

Or using `uv`:

```json
{
  "mcpServers": {
    "reclass": {
      "command": "uv",
      "args": ["run", "--directory", "/path/to/ReClass.NET-MCP/ReClassMCP.Server", "python", "reclass_mcp_server.py"],
      "env": {}
    }
  }
}
```

## Available MCP Tools

### Connection & Status

| Tool | Description |
|------|-------------|
| `IsConnected` | Check if ReClass.NET plugin is available |
| `GetStatus` | Get current status including attached process info |
| `GetProcessInfo` | Get detailed process information |

### Memory Operations

| Tool | Description |
|------|-------------|
| `ReadMemory` | Read memory from the attached process |
| `WriteMemory` | Write memory to the attached process |
| `ParseAddress` | Parse an address formula (e.g., `module.exe+0x1234`) |

### Module & Section Info

| Tool | Description |
|------|-------------|
| `GetModules` | List all loaded modules in the process |
| `GetSections` | List all memory sections |

### Class/Structure Management

| Tool | Description |
|------|-------------|
| `GetClasses` | List all classes in the project |
| `GetClass` | Get detailed info about a specific class |
| `GetNodes` | Get nodes/fields in a class, with optional paging/range/padding filters |
| `CreateClass` | Create a new class/structure |
| `AddNode` | Add a new field to a class |
| `AddNodeAtOffset` | Add a new field at an exact byte offset, padding gaps as needed |
| `AddClassInstanceAtOffset` | Add a nested class/structure field at an exact byte offset |
| `RenameNode` | Rename a field |
| `SetComment` | Set a comment on a field |
| `ChangeNodeType` | Change a field's type |

### Supported Node Types

- **Numeric**: `int8`, `int16`, `int32`, `int64`, `uint8`, `uint16`, `uint32`, `uint64`, `float`, `double`
- **Hex**: `hex8`, `hex16`, `hex32`, `hex64`
- **Text**: `utf8text`, `utf16text`, `utf32text`, `utf8textptr`, `utf16textptr`, `utf32textptr`
- **Vector**: `vector2`, `vector3`, `vector4`
- **Matrix**: `matrix3x3`, `matrix3x4`, `matrix4x4`
- **Other**: `pointer`, `function`, `functionptr`, `virtualmethodtable`, `bool`

## Usage Example

Once configured, you can ask your MCP client to:

```
"Connect to ReClass.NET and show me the current classes"

"Read 64 bytes of memory at 0x7FF12345"

"Create a new class called 'ConfigData' and add fields for flags (uint32) and count (int32)"

"Analyze the memory at 0x7FF12345 and suggest appropriate field types"
```

For very large sparse classes, avoid unbounded class dumps. Use `GetClasses` for
summary information, then call `GetNodes` with `include_padding=false`, `limit`,
and optionally `offset`/`size` to inspect only the relevant fields.

## License

MIT License
