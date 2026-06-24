# Unity Editor HTTP Server

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
https://github.com/YOUR_USERNAME/unity-editor-http-server.git
```

Or add to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.devtools.unity-editor-http-server": "https://github.com/YOUR_USERNAME/unity-editor-http-server.git"
  }
}
```

The server starts automatically when the Unity Editor opens (via `[InitializeOnLoad]`). Look for:

```
[TestHttpServer] Listening on http://localhost:8765/
```

in the Unity console.

## Requirements

- Unity 2021.3 or newer
- `com.unity.test-framework` package (included by default in Unity projects)

## AI skills

The `skills/` directory contains ready-made integration files:

- **`skills/claude-skill.md`** — Drop into a Claude Code project's `.claude/skills/` directory (or any skills directory). Teaches Claude how to use the API and when to invoke each endpoint.
- **`skills/mistral-skill.md`** — System prompt and function-calling schema for Mistral. Includes Python tool implementations mapping function names to HTTP calls.

## Port

The server binds to port `8765`. If that port is in use, an error is logged to the Unity console and the server stays inactive. The port is defined as `const int Port = 8765` in `Editor/TestHttpServer.cs`.

## Notes

- Edit-mode tests only. PlayMode tests are not supported.
- The server is Editor-only and is stripped from builds.
- Compiler messages in `/editor/status` are populated by the `CompilationPipeline.assemblyCompilationFinished` event — they will be empty until Unity recompiles after the server starts. An empty `compilerMessages` array does not mean "no errors"; it means "no compile has happened yet."
