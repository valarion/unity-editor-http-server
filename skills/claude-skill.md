---
name: unity-editor-http-server
description: Use when working on a Unity project that has the Unity Editor HTTP Server package installed. Teaches you how to interact with Unity programmatically via HTTP — checking editor state, triggering recompilation, and running tests — enabling a fully autonomous write-compile-test loop without human intervention.
---

# Unity Editor HTTP Server

The Unity Editor runs an HTTP server on **`http://localhost:8765`** when this package is installed. Use it to observe and control Unity without touching the GUI.

## Core workflow

This is the loop you use when writing or modifying C# code in a Unity project:

```
1. Write .cs files to disk (in the project's Assets/ folder)
2. POST /assets/refresh          — tell Unity to pick up the new files
3. Poll GET /editor/status       — repeat every ~500ms until isCompiling == false
4. Check compilerMessages        — if any have type "Error", stop and fix them
5. POST /tests/run {"minimal":true}  — run tests
6. On failure: GET /logs?level=error — get additional context
7. POST /logs/clear              — optional, start fresh before next iteration
```

## Endpoints

### GET /editor/status
Returns the full editor state. **Poll this after every asset refresh.**

```json
{
  "isCompiling": false,
  "isPlaying": false,
  "isPaused": false,
  "unityVersion": "6000.5.0f1",
  "productName": "MyGame",
  "assemblies": [
    {
      "name": "MyGame.Data",
      "outputPath": "Library/ScriptAssemblies/MyGame.Data.dll",
      "compilerMessages": [
        { "type": "Error", "message": "CS0246: ...", "file": "Assets/Scripts/Foo.cs", "line": 42, "column": 7 }
      ]
    }
  ]
}
```

**Decision rule:** if `isCompiling == true`, wait and retry. If any `compilerMessages[].type == "Error"`, fix the code before running tests.

---

### POST /assets/refresh
Triggers `AssetDatabase.Refresh()` on Unity's main thread. Call this immediately after writing any `.cs` files to disk.

```
POST http://localhost:8765/assets/refresh
→ { "refreshed": true, "wasCompiling": false }
```

If `wasCompiling` is true, a compile was already in progress — you still need to poll `/editor/status` until the next compile cycle finishes.

---

### POST /tests/run
Runs edit-mode tests. Supports glob wildcards.

```json
// Run all tests, minimal output (recommended for AI)
{ "minimal": true }

// Run tests matching a wildcard
{ "tests": ["*MyService*"], "minimal": true }

// Run a specific test by exact full name
{ "tests": ["My.Namespace.MyTests.SpecificTest"] }
```

**Minimal response** (use this — it's far shorter):
```json
{ "total": 10, "passed": 9, "failed": 1, "errors": [
    { "name": "My.Namespace.MyTests.FailingTest", "message": "Expected 1 but was 2", "stackTrace": "..." }
]}
```

**Full response** (omit `minimal` or set to false):
```json
{ "results": [
    { "name": "...", "status": "Passed", "duration": 0.0012, "message": null }
]}
```

---

### GET /tests/list
Returns all discovered edit-mode test full names as a JSON array. Use this to understand what tests exist before running a subset.

---

### GET /logs?level=error&limit=50
Returns the rolling console log buffer (last 500 entries). Essential for diagnosing compile errors or runtime exceptions.

- `level`: `all` (default), `error`, `warning`
- `limit`: max entries to return (default 100)

```json
{ "count": 2, "entries": [
    { "timestamp": "2026-06-24T10:05:11Z", "level": "error",
      "message": "NullReferenceException", "stackTrace": "at Foo.Bar..." }
]}
```

---

### POST /logs/clear
Clears the log buffer. Call before starting a new iteration to avoid noise from previous runs.
```
→ { "cleared": 47 }
```

---

### GET /assets/list?type=Script
Enumerates project assets by type. Useful for discovering what files exist before reading or modifying them.

Type values: `Script`, `Prefab`, `ScriptableObject`, `Scene`, `Texture`, `Material`, `AudioClip`, `Animation`. Omit `type` for all assets.

```json
{ "count": 3, "assets": [
    { "guid": "a1b2c3d4", "path": "Assets/Scripts/MyService.cs" }
]}
```

---

### GET /swagger
Opens the Swagger UI in a browser. Useful for manually exploring the API.

## Practical notes

- **Never run tests before confirming `isCompiling == false`** — tests will run against stale assemblies.
- **Compiler messages are only populated after the first compile** that occurs while the server is running. On a fresh Unity open, they'll be empty until Unity recompiles.
- **The server only runs in the Unity Editor** — not in builds.
- **Port 8765 is hardcoded.** If it's already in use, the server logs an error and stays inactive.
- Tests run in **EditMode** only. PlayMode tests are not supported.
