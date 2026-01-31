# Repository Guidelines for AI Agents

This document provides comprehensive guidance for agentic AI tools (Claude Code, OpenAI Codex, etc.) working in the GONet codebase. Follow these guidelines to effectively navigate, understand, and modify this networked multiplayer framework.

---

## Quick Reference

| Item | Location/Value |
|------|----------------|
| **Unity Version** | 2022.3.12f1 |
| **GONet Runtime Log Directory** | `<persistentDataPath>/logs` (see platform paths below) |
| **Log File Pattern** | `gonet-YYYY-MM-DD.log` or `<processId>-gonet-YYYY-MM-DD.log` |
| **GONet Source** | `Assets/GONet/Code/GONet/` |
| **Generated Code** | `Assets/GONet/Code/GONet/Generation/Generated/` |
| **Unit Tests** | `Assets/GONet/Code/GONet/Editor/UnitTests/` |
| **Main Entry Points** | `GONetMain`, `GONetGlobal`, `GONetParticipant` |

---

## GONet Log Files

### Log File Locations

GONet uses `Application.persistentDataPath` for log storage. The exact path varies by platform and project settings:

**Windows (Standalone/Editor):**
```
C:\Users\<username>\AppData\LocalLow\<CompanyName>\<ProductName>\logs\
```

**macOS:**
```
~/Library/Application Support/<CompanyName>/<ProductName>/logs/
```

**Linux:**
```
~/.config/unity3d/<CompanyName>/<ProductName>/logs/
```

### Log File Naming

- **Single instance:** `gonet-2025-01-12.log`
- **Multiple instances (server + clients):** `<processId>-gonet-2025-01-12.log`
- **Profile-specific logs:** `gonet-<ProfileName>-2025-01-12.log`

### When to Check Logs

**IMPORTANT: After every test run, check the GONet logs for:**
1. Network sync errors or warnings
2. RPC delivery failures
3. Spawn/despawn issues
4. Authority and ownership problems
5. SoA (Structure-of-Arrays) blending pipeline issues
6. Transport-level errors (Steamworks or NetcodeIO)

### Log Level Definitions

| Level | Compile Flag | Use Case |
|-------|--------------|----------|
| `Verbose` | `LOG_VERBOSE` | Detailed tracing, performance analysis |
| `Debug` | `LOG_DEBUG` | Development diagnostics |
| `Info` | `LOG_INFO` | Normal operation milestones |
| `Warning` | `LOG_WARNING` | Recoverable issues |
| `Error` | `LOG_ERROR` | Failures requiring attention |
| `Fatal` | `LOG_FATAL` | Critical failures |

### Programmatic Log Access

```csharp
// Get the log directory path
string logDir = GONetLog.GetLogDirectory();

// Get log directory statistics
var info = GONetLog.GetLogDirectoryInfo();
// info.TotalFileCount, info.TotalSizeMB, info.LogFileCount, etc.

// Runtime log control
GONetLog.IsEnabled = true;           // Enable/disable all logging
GONetLog.MinimumLogLevel = LogLevel.Info;  // Filter by level
```

---

## Project Structure & Module Organization

### Directory Layout

```
Assets/
├── Code/                           # Gameplay scripts (player, weapon, network glue)
├── GONet/
│   └── Code/
│       └── GONet/
│           ├── Config/             # Configuration (GONetConfig.cs)
│           ├── Core/               # Core utilities and transport abstractions
│           │   ├── Analysis/       # Blending quality analyzers
│           │   ├── PluginAPI/      # Extension points for custom blending
│           │   ├── Transport/      # Transport interfaces
│           │   └── Utils/          # Utilities (GONetLog, BitConverter, etc.)
│           ├── Editor/             # Unity Editor tooling
│           │   ├── Generation/     # Code generators
│           │   └── UnitTests/      # Editor-mode unit tests
│           ├── Generation/         # Runtime-generated code (DO NOT EDIT)
│           │   └── Generated/      # Auto-generated SoA descriptors, sync events
│           ├── Jobs/               # Unity Jobs/Burst-compiled operations
│           ├── Main/               # Primary GONet runtime
│           │   └── DistributedHost/# Host migration, failover, gossip
│           ├── Rpc/                # RPC infrastructure
│           ├── Sample/             # Example implementations
│           ├── Tests/              # PlayMode tests
│           └── Transports/         # Transport implementations
│               ├── NetcodeIO/      # NetcodeIO transport
│               └── Steamworks/     # Steamworks transport
├── Prefabs/                        # Networked prefabs
├── Scenes/                         # Game scenes
└── Resources/GONet/                # Runtime-loaded GONet assets
```

### Key Files to Understand First

| File | Purpose |
|------|---------|
| `Main/GONet.cs` | Central networking hub - sync, spawn, events |
| `Main/GONetMain.cs` | Static entry point, time management |
| `Core/GONetParticipant.cs` | Component for networked GameObjects |
| `Core/GONetEvents.cs` | Event system (IGONetEvent, ITransientEvent, IPersistentEvent) |
| `Main/GONetRpcs.cs` | RPC attributes and infrastructure |
| `Core/Utils/GONetLog.cs` | Logging system |
| `Generation/Generated/GONet_SoA_Descriptor.cs` | Generated sync value descriptors |
| `Config/GONetConfig.cs` | Configuration constants |

---

## Core GONet Concepts

### GONetParticipant

Every networked GameObject must have a `GONetParticipant` component. This is GONet's equivalent to NetworkBehaviour in other frameworks.

```csharp
// Every networked object has:
// - GONetId: Unique identifier across the network
// - OwnerAuthorityId: Which machine owns/controls this object
// - CodeGenerationId: Links to generated sync code
```

### Auto-Magical Sync ([GONetAutoMagicalSync])

Mark fields/properties for automatic network synchronization:

```csharp
[GONetAutoMagicalSync]
public float Health { get; set; }

[GONetAutoMagicalSync(SyncChangesEverySeconds = 0.1f)]
public Vector3 Position { get; set; }
```

### RPC System

Three RPC types with different routing:

```csharp
[ServerRpc]           // Client → Server
void RequestSpawn() { }

[ClientRpc]           // Server → All Clients
void NotifyDeath() { }

[TargetRpc]           // Server → Specific Client
void SendPrivateMessage(string msg) { }
```

### Event System

GONet uses an event bus for decoupled communication:

```csharp
// Subscribe to sync events
GONetMain.EventBus.Subscribe<SyncEvent_GONetParticipant_OwnerAuthorityId>(OnOwnerChanged);

// Event types:
// - ITransientEvent: One-time, not stored (poolable)
// - IPersistentEvent: Stored for late-joiners (NEVER pool these!)
```

### SoA Blending Pipeline (v2)

High-performance interpolation using Structure-of-Arrays pattern:
- Lock-free ring buffers for value streams
- Burst-compiled parallel jobs
- Batched Transform writes
- 6-9x CPU reduction vs. v1 event-driven approach

---

## Build, Test, and Development Commands

### Unity Editor Operations

```bash
# Open project in Unity Hub with correct version
# Check ProjectSettings/ProjectVersion.txt if unsure about version

# CLI build (Windows x86_64)
Unity -batchmode -nographics -projectPath . -buildTarget StandaloneWindows64 -quit -logFile Logs/build.log

# Run PlayMode tests headless
Unity -batchmode -nographics -projectPath . -runTests -testPlatform PlayMode -logFile Logs/test-playmode.log

# Run EditMode tests headless
Unity -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -logFile Logs/test-editmode.log
```

### After Running Tests

**Always check GONet logs after test runs:**

1. Navigate to log directory (use `GONetLog.GetLogDirectory()` or check `Application.persistentDataPath + "/logs"`)
2. Open the most recent `gonet-*.log` file
3. Search for `[Log:Error]` and `[Log:Warning]`
4. Look for patterns like:
   - `RPC delivery failed`
   - `Sync event mismatch`
   - `GONetId not found`
   - `Authority violation`
   - `SoA pipeline` errors

---

## Coding Style & Naming Conventions

### C# Style

- **Indentation:** 4 spaces (no tabs)
- **One public class per file** matching the MonoBehaviour/ScriptableObject name
- **Types/Methods:** PascalCase
- **Locals/Parameters:** camelCase
- **Private serialized fields:** `_camelCase` when distinction needed

### Namespaces

Stay within existing namespaces:
- `GONet` - Main runtime
- `GONet.Core` - Core utilities
- `GONet.Utils` - Helper utilities
- `GONet.Generation` - Generated code
- `GONet.PluginAPI` - Extension points

### Generated Code Policy

**NEVER manually edit files in:**
- `Assets/GONet/Code/GONet/Generation/Generated/`
- `Assets/GONet/Code/GONet/Generation/GONetParticipant_AutoMagicalSyncCompanion_Generated.cs`

These are regenerated when GONetParticipant prefabs change. Manual edits will be lost.

---

## Testing Guidelines

### Test Organization

| Location | Type | Purpose |
|----------|------|---------|
| `Editor/UnitTests/GONet/` | EditMode | Fast unit tests, no scene required |
| `Editor/UnitTests/GONet/DistributedHost/` | EditMode | Host migration, failover tests |
| `Tests/` | PlayMode | Full networking scenarios |

### Test Naming Conventions

```csharp
// Class: <Feature>Tests
public class GONetIdBatchManagerTests { }

// Methods: <Action>_<Condition>_<Expected>
[Test]
public void Handles_ClientDisconnectGracefully_WhenMidSync() { }
```

### Writing Tests for GONet

```csharp
// For unit tests that need synchronous logging (LogAssert.Expect):
var profile = new GONetLog.LoggingProfile("TestProfile",
    useSynchronousLogging: true);
GONetLog.RegisterLoggingProfile(profile);

// Test TearDown
[TearDown]
public void TearDown()
{
    GONetLog.FlushQueuedLogs();
    GONetLog.UnregisterLoggingProfile("TestProfile");
}
```

### Bug Fix Protocol

When fixing a bug:
1. Write a failing test that reproduces the bug
2. Implement the fix
3. Verify the test passes
4. Check GONet logs for unexpected warnings/errors
5. Update AGENTS.md if the fix reveals new patterns

---

## Commit & Pull Request Guidelines

### Commit Style

Recent commits use descriptive, present-tense summaries:

```
[GONet] Fix: Late-joiner SoA sync for non-physics objects at rest
[GONet] Fix: ServerRpc→TargetRpc response pattern no longer incorrectly skipped
[GONet] Add runtime logging controls: IsEnabled and MinimumLogLevel
```

### PR Requirements

Each PR should include:
1. **What changed** - Summary of modifications
2. **Why** - Problem being solved or feature being added
3. **Test evidence** - Commands run, log excerpts showing success
4. **Platform notes** - Any build targets affected
5. **GONet protocol changes** - Explicitly call out serialization/schema changes

---

## Security & Configuration Tips

### Never Commit

- `Library/`, `Temp/`, local build artifacts
- Third-party plugin modifications (stage only intentional edits)
- Hardcoded network ports, API keys, or secrets

### Network Security Practices

- Guard new RPCs with validation and logging
- Document ports/keys in private channels, not the repo
- Networked features rely on deterministic state - be careful with authority

---

## Common Debugging Scenarios

### Sync Values Not Updating

1. Check `[GONetAutoMagicalSync]` attribute is present
2. Verify `GONetParticipant` component exists
3. Check authority - only the owner can change synced values
4. Look in logs for `SyncEvent` errors

### RPC Not Being Received

1. Verify RPC method has correct attribute (`[ServerRpc]`, `[ClientRpc]`, `[TargetRpc]`)
2. Check parameter types are MemoryPack-compatible (see `GONetRpcs.cs` comments)
3. Look for `RPC delivery` messages in logs
4. Ensure `IsReliable = true` if guaranteed delivery needed

### Late-Joiner Missing State

1. Check if event implements `IPersistentEvent` (stored for late-joiners)
2. Verify persistent events are NOT using object pooling
3. Look for `Server_SendClientPersistentEventsSinceStart` in logs

### SoA Blending Issues

1. Check `GONetMain.SoAData` for pipeline state
2. Look for `SoA_BlendingPipeline` or `SoA_ValueApplicator` log messages
3. Verify object has non-authority status (authority doesn't blend)

---

## Agent Workflow Recommendations

### Starting a Task

1. Read this AGENTS.md first
2. Identify relevant source files using the directory structure above
3. Check existing tests for the area you're modifying
4. Review recent commits in the affected area

### During Development

1. Follow existing code patterns in the file you're editing
2. Add logging with appropriate levels for new code paths
3. Consider both server and client execution paths
4. Remember: generated code will be overwritten

### Before Completing

1. Run relevant tests
2. **Check GONet logs** in `<persistentDataPath>/logs/` directory
3. Search logs for errors/warnings related to your changes
4. Ensure no regression in existing functionality

---

## Frequently Needed Patterns

### Creating a New Networked Object

```csharp
// 1. Add GONetParticipant component to prefab
// 2. Add GONetAutoMagicalSync to synced fields
// 3. Regenerate (Unity menu: GONet > Regenerate Code)
// 4. Use GONetMain.Instantiate() for spawning
```

### Handling Authority Transfer

```csharp
GONetMain.EventBus.Subscribe<SyncEvent_GONetParticipant_OwnerAuthorityId>(e =>
{
    if (e.GONetParticipant == myGNP && e.ValueNew == GONetMain.MyAuthorityId)
    {
        // I now have authority
    }
});
```

### Checking Network Role

```csharp
if (GONetMain.IsServer) { /* Server-only logic */ }
if (GONetMain.IsClient) { /* Client-only logic */ }
if (gnp.IsMine) { /* I own this object */ }
```

---

## Version History

- **v1.6** (Current) - SoA blending pipeline, enhanced logging, distributed host support
- **v1.5** - MemoryPack serialization, improved RPC system
- **v1.4** - Steamworks transport, scene management

---

*Last updated: January 2025*
