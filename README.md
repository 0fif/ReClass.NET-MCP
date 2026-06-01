# ReClass.NET MCP Integration

This project provides an MCP (Model Context Protocol) integration for ReClass.NET, allowing Claude Code to interact with ReClass.NET for memory analysis and reverse engineering tasks.

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
- Exposes tools for Claude Code to use

## Compatibility

The C# plugin must be built against the same ReClass.NET code that you run.

Downloading the official ReClass.NET `v1.2` release zip is not the same as
cloning ReClass.NET and building current `master`. The release zip is old tagged
code from 2019. Current `master` is newer unreleased source code.

Do not build the plugin against current `master` and then load it into the old
`v1.2` release zip. Build and run them from the same ReClass.NET checkout.

ReClass.NET may still report assembly version `1.2.0.0` across incompatible
source snapshots, so the assembly version alone is not a compatibility check.

There is no newer official release number than `v1.2` right now, but there is
newer source code. This matters because the `v1.2` release zip and current source
can both identify as `1.2.0.0` while exposing different APIs:

```text
official v1.2 release zip -> ClassNode.Uuid is NodeUuid
current master source     -> ClassNode.Uuid is Guid
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

### Configuring Claude Code

Add the following to your Claude Code MCP configuration file (`~/.claude/mcp.json` or project `.claude/mcp.json`):

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
| `GetNodes` | Get all nodes/fields in a class |
| `CreateClass` | Create a new class/structure |
| `AddNode` | Add a new field to a class |
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

Once configured, you can ask Claude Code to:

```
"Connect to ReClass.NET and show me the current classes"

"Read 64 bytes of memory at 0x7FF12345"

"Create a new class called 'ConfigData' and add fields for flags (uint32) and count (int32)"

"Analyze the memory at 0x7FF12345 and suggest appropriate field types"
```

## License

MIT License
