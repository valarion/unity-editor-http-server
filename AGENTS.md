# Agent Instructions — Unity Editor HTTP Server

This file tells AI coding agents how to work in this repository.

## What this project is

A Unity Editor-only package (`com.devtools.unity-editor-http-server`) containing a single C# file that starts an HTTP server inside the Unity Editor. The server exposes Unity's TestRunner, AssetDatabase, and console log over `localhost:8765` so AI agents can drive a write-compile-test loop without touching the Unity GUI.

## Repository layout

```
Editor/
  TestHttpServer.cs                         ← All server logic. One file, one static class.
  com.devtools.unity-editor-http-server.asmdef
skills/
  claude-skill.md                           ← Claude Code skill (copy to .claude/skills/)
  mistral-skill.md                          ← Mistral system prompt + function-calling schema
package.json                                ← Unity Package Manager manifest
AGENTS.md                                   ← this file
CLAUDE.md                                   ← Claude Code specific instructions
README.md
```

## How to develop

This repo contains a Unity package, not a Unity project. You cannot run or test the code directly from this repo. To develop:

1. Open a Unity project that has this package installed (or add it via a local path reference).
2. Edit `Editor/TestHttpServer.cs` here.
3. In Unity, the package will recompile automatically if installed via a local path (`"file:../../path/to/unity-editor-http-server"`).
4. Verify your changes by querying `http://localhost:8765/editor/status` or `http://localhost:8765/swagger`.

## Key constraints

- **One file.** All server logic lives in `Editor/TestHttpServer.cs`. Do not split it into multiple files unless there is a compelling reason — the single-file design makes the package easy to audit and copy.
- **No external dependencies.** The package deliberately avoids NuGet packages or additional Unity packages beyond `com.unity.test-framework`. All JSON is hand-built with `StringBuilder`. Do not add `Newtonsoft.Json` or similar.
- **Editor-only.** The `[InitializeOnLoad]` attribute and `HttpListener` are Editor-only. Never add runtime (non-Editor) code.
- **No config files.** Port (`8765`) and buffer size (`500` logs) are constants. If you change them, change the constants in code — do not add a config asset.

## The main thread pattern

Unity APIs (`AssetDatabase`, `EditorApplication`, `TestRunnerApi`, `PlayerSettings`, etc.) must be called on Unity's main thread. The server handles requests on background threads. The bridge is:

```csharp
// From a background thread — enqueue work and wait for the result:
var ready = new ManualResetEventSlim(false);
_mainQueue.Enqueue(() => {
    // ... call Unity API here ...
    ready.Set();
});
ready.Wait();
```

`FlushMainQueue()` is hooked to `EditorApplication.update` and drains the queue each frame. Preserve this pattern when adding new endpoints that touch Unity APIs. Endpoints that only read thread-safe state (`ConcurrentQueue.ToArray()`) do not need it.

## Adding a new endpoint

1. Add a route in `Route()` matching the new path and HTTP method.
2. Implement a `HandleXxx(HttpListenerContext ctx)` method.
3. Use `_mainQueue` + `ManualResetEventSlim` if the handler needs Unity APIs.
4. Call `Respond(ctx, 200, json)` to send the response.
5. Add the new path to `OpenApiSpec` (the inline JSON string constant).
6. Update `skills/claude-skill.md` and `skills/mistral-skill.md` with the new endpoint.

## Testing a change

Without a Unity project available, you can verify logic by reading the code. The request parsing helpers (`ParseStringArray`, `ParseBool`) and JSON serialization (`JsonStr`, `BoolStr`, `BuildJsonArray`) are pure C# and have no Unity dependencies — they can be tested in isolation.

With a Unity project:
```bash
# Check the server is running
curl http://localhost:8765/editor/status

# Run all tests, compact output
curl -X POST http://localhost:8765/tests/run -H "Content-Type: application/json" -d "{\"minimal\":true}"

# Check recent errors
curl "http://localhost:8765/logs?level=error&limit=20"
```

## Updating the skills

When you change the API (new endpoint, changed response shape, new query parameter), update both skill files:

- `skills/claude-skill.md` — update the endpoint table and workflow section
- `skills/mistral-skill.md` — update the system prompt block and the function-calling JSON schema

These files are the primary documentation for AI consumers of this API. Keep them accurate.
