# Unity Editor HTTP Server

> **This package was designed and implemented through an AI-assisted development session using [Claude](https://claude.ai) (Anthropic). The architecture, all C# code, the OpenAPI spec, and the AI skill files were produced by the model from a conversational prompt, with a human reviewing and directing the work. No code was written by hand.**

A Unity Editor package that exposes an HTTP API on `localhost:8765`, giving AI agents (and other tools) programmatic access to editor state, the TestRunner, the console log, and AssetDatabase — with no GUI interaction required.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET`  | `/editor/status` | Compilation state, play mode, Unity version, per-assembly compiler errors |
| `POST` | `/assets/refresh` | Trigger `AssetDatabase.Refresh()` after writing files to disk |
| `GET`  | `/assets/list` | Enumerate project assets by type |
| `GET`  | `/tests/list` | All discovered edit-mode test names |
| `POST` | `/tests/run` | Run tests with optional glob filter and minimal output mode |
| `GET`  | `/logs` | Rolling console log buffer (last 500 entries) |
| `POST` | `/logs/clear` | Clear the log buffer |
| `GET`  | `/swagger` | Swagger UI |
| `GET`  | `/swagger/openapi.json` | OpenAPI 3.0 spec |

## AI workflow

```
1. Write .cs files to Assets/
2. POST /assets/refresh
3. Poll GET /editor/status every 500ms until isCompiling == false
4. Check assemblies[*].compilerMessages for type == "Error" — fix and repeat if any
5. POST /tests/run {"minimal": true}
6. On failure: GET /logs?level=error for more context
```

## Installation

In Unity's Package Manager, choose **Add package from git URL** and enter:

```
https://github.com/valarion/unity-editor-http-server.git
```

Or add to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.devtools.unity-editor-http-server": "https://github.com/valarion/unity-editor-http-server.git"
  }
}
```

The server starts automatically when the Unity Editor opens (via `[InitializeOnLoad]`). Look for:

```
[TestHttpServer] Listening on http://localhost:8765/
```

in the Unity console.

## Local development

To iterate on the package without pushing to git, point your Unity project at the local folder instead of the git URL. In `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.devtools.unity-editor-http-server": "file:G:/unity-editor-http-server"
  }
}
```

Edit `Editor/TestHttpServer.cs` directly — Unity recompiles automatically on save. When satisfied, push to git and switch the manifest entry back to the git URL.

## Adding custom endpoints

You can add endpoints from your own project without modifying the package. This is the recommended way to prototype new endpoints before contributing them upstream.

**1. Create an Editor script in your project:**

```csharp
using UnityEditor;

[InitializeOnLoad]
public static class MyEndpoints
{
    static MyEndpoints()
    {
        TestHttpServer.RegisterEndpoint("GET", "/my/data", ctx =>
        {
            // Unity APIs must run on the main thread — use RunOnMainThread:
            var value = TestHttpServer.RunOnMainThread(() => MyService.GetValue());
            TestHttpServer.Respond(ctx, 200, $"{{\"value\":{TestHttpServer.JsonStr(value)}}}");
        });
    }
}
```

**2. Test it:**
```bash
curl http://localhost:8765/my/data
```

**3. Contribute it upstream** by opening a PR to this repository.

See `Samples~/CustomEndpoints/CustomEndpointExample.cs` for more examples including POST with a request body and main-thread dispatch.

### Extension API reference

| Member | Description |
|--------|-------------|
| `RegisterEndpoint(method, path, handler)` | Register a custom route. Built-in routes take precedence. |
| `RunOnMainThread(Action)` | Dispatch work to Unity's main thread and block until done. |
| `RunOnMainThread<T>(Func<T>)` | Same, but returns a value. |
| `Respond(ctx, status, body, contentType?)` | Write an HTTP response. |
| `JsonStr(string)` | Escape and quote a string as a JSON literal, or `"null"`. |

## Requirements

- Unity 2021.3 or newer
- `com.unity.test-framework` package (included by default in Unity projects)

## AI skills

The `skills/` directory contains ready-made integration files:

- **`skills/claude-skill.md`** — Drop into a Claude Code project's `.claude/skills/` directory (or any skills directory). Teaches Claude how to use the API and when to invoke each endpoint.
- **`skills/mistral-skill.md`** — System prompt and function-calling schema for Mistral. Includes Python tool implementations mapping function names to HTTP calls.

## Port

The default port is `8765`. You can change it (and toggle auto-start) in **Tools > HTTP Server > Settings**. The port is persisted in `EditorPrefs` and survives recompiles. If the port is unavailable the server logs an error and stays inactive.

## Notes

- Edit-mode tests only. PlayMode tests are not supported.
- The server is Editor-only and is stripped from builds.
- Compiler messages in `/editor/status` are populated by the `CompilationPipeline.assemblyCompilationFinished` event — they will be empty until Unity recompiles after the server starts. An empty `compilerMessages` array does not mean "no errors"; it means "no compile has happened yet."
