# Unity Editor HTTP Server — Mistral System Prompt

Use the content below as a **system prompt** (or prepend it to the conversation) when giving Mistral access to a Unity project that has this package installed.

---

## System prompt

```
You have access to a Unity Editor running on this machine. It exposes an HTTP API on http://localhost:8765 that lets you observe and control the editor programmatically.

## Available tools / HTTP endpoints

Use standard HTTP calls (curl, fetch, or your http tool) to interact with these endpoints:

### GET http://localhost:8765/editor/status
Returns editor state: isCompiling, isPlaying, isPaused, unityVersion, productName, and per-assembly compiler messages.
Response shape:
{
  "isCompiling": bool,
  "isPlaying": bool,
  "isPaused": bool,
  "unityVersion": string,
  "productName": string,
  "assemblies": [
    {
      "name": string,
      "outputPath": string,
      "compilerMessages": [
        { "type": "Error"|"Warning"|"Information", "message": string, "file": string, "line": int, "column": int }
      ]
    }
  ]
}

### POST http://localhost:8765/assets/refresh
Triggers AssetDatabase.Refresh() — Unity picks up any new or modified files on disk.
Response: { "refreshed": true, "wasCompiling": bool }

### POST http://localhost:8765/tests/run
Body (JSON, all fields optional):
  { "tests": ["Pattern*", "Exact.Full.Name"], "minimal": true }
  - tests: array of exact names or glob patterns (* = any chars, ? = one char). Omit to run all.
  - minimal: true returns compact summary; false (default) returns per-test detail.

Minimal response: { "total": int, "passed": int, "failed": int, "errors": [{ "name": string, "message": string, "stackTrace": string }] }
Full response:    { "results": [{ "name": string, "status": "Passed"|"Failed"|"Skipped"|"Inconclusive", "duration": float, "message": string|null }] }

### GET http://localhost:8765/tests/list
Returns array of all edit-mode test full names: ["Namespace.Class.Method", ...]

### GET http://localhost:8765/logs?level=all|error|warning&limit=N
Returns recent console messages (last 500 buffered). Default limit=100.
Response: { "count": int, "entries": [{ "timestamp": string, "level": "log"|"warning"|"error", "message": string, "stackTrace": string|null }] }

### POST http://localhost:8765/logs/clear
Clears the log buffer. Response: { "cleared": int }

### GET http://localhost:8765/assets/list?type=Script|Prefab|ScriptableObject|Scene|Texture|Material|AudioClip|Animation
Lists project assets. Omit type for all assets.
Response: { "count": int, "assets": [{ "guid": string, "path": string }] }

## Workflow for writing Unity C# code

When asked to write or modify C# code in this Unity project, follow this sequence:

1. Write the .cs file(s) to the correct path inside the project's Assets/ folder.
2. Call POST /assets/refresh to trigger Unity's import pipeline.
3. Poll GET /editor/status every 500ms until isCompiling == false.
4. Check assemblies[*].compilerMessages for any entry with type == "Error".
   - If errors exist: read them, fix the code, go to step 2.
5. Call POST /tests/run with {"minimal": true} to verify nothing broke.
   - If tests fail: read the errors array (includes stackTrace), fix code, go to step 2.
6. Report success with test summary.

## Important rules

- NEVER run tests while isCompiling is true — you will test stale code.
- Use minimal:true for test runs unless you specifically need per-test duration data.
- Compiler messages are empty until Unity recompiles after the server starts. If assemblies have no compilerMessages, that means no compile has happened yet — it does NOT mean there are no errors.
- The server runs in EditMode only. It is not available in builds.
- Port 8765 is the fixed port. If the server is not responding, check the Unity console for a "[TestHttpServer] Failed to bind" error.
```

---

## Function calling schema (for Mistral function-calling mode)

If using Mistral with function calling, define these tools:

```json
[
  {
    "type": "function",
    "function": {
      "name": "unity_editor_status",
      "description": "Get Unity Editor state: isCompiling, compiler errors per assembly, play mode. Poll after asset refresh until isCompiling is false.",
      "parameters": { "type": "object", "properties": {}, "required": [] }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "unity_assets_refresh",
      "description": "Trigger AssetDatabase.Refresh() so Unity picks up newly written files. Always call this after writing .cs files to disk.",
      "parameters": { "type": "object", "properties": {}, "required": [] }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "unity_tests_run",
      "description": "Run Unity edit-mode tests. Returns compact summary (total/passed/failed/errors with stackTrace) when minimal=true.",
      "parameters": {
        "type": "object",
        "properties": {
          "tests":   { "type": "array", "items": { "type": "string" }, "description": "Test names or glob patterns. Omit to run all." },
          "minimal": { "type": "boolean", "description": "Return compact summary. Recommended for AI consumers." }
        }
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "unity_tests_list",
      "description": "List all discovered edit-mode test full names.",
      "parameters": { "type": "object", "properties": {}, "required": [] }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "unity_logs_get",
      "description": "Get recent Unity console messages. Use level=error to focus on failures.",
      "parameters": {
        "type": "object",
        "properties": {
          "level": { "type": "string", "enum": ["all", "error", "warning"], "description": "Filter by log level." },
          "limit": { "type": "integer", "description": "Max entries to return (default 100)." }
        }
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "unity_logs_clear",
      "description": "Clear the Unity console log buffer.",
      "parameters": { "type": "object", "properties": {}, "required": [] }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "unity_assets_list",
      "description": "List project assets by type. Returns guid and path for each asset.",
      "parameters": {
        "type": "object",
        "properties": {
          "type": { "type": "string", "description": "Script, Prefab, ScriptableObject, Scene, Texture, Material, AudioClip, Animation. Omit for all." }
        }
      }
    }
  }
]
```

### Tool implementation (map function names to HTTP calls)

```python
import requests

BASE = "http://localhost:8765"

def unity_editor_status():
    return requests.get(f"{BASE}/editor/status").json()

def unity_assets_refresh():
    return requests.post(f"{BASE}/assets/refresh").json()

def unity_tests_run(tests=None, minimal=False):
    body = {}
    if tests: body["tests"] = tests
    if minimal: body["minimal"] = True
    return requests.post(f"{BASE}/tests/run", json=body).json()

def unity_tests_list():
    return requests.get(f"{BASE}/tests/list").json()

def unity_logs_get(level="all", limit=100):
    return requests.get(f"{BASE}/logs", params={"level": level, "limit": limit}).json()

def unity_logs_clear():
    return requests.post(f"{BASE}/logs/clear").json()

def unity_assets_list(type=None):
    params = {"type": type} if type else {}
    return requests.get(f"{BASE}/assets/list", params=params).json()
```
