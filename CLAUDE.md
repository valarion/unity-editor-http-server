# Claude Code Instructions — Unity Editor HTTP Server

## Project summary

Single-file Unity Editor package. One static C# class (`TestHttpServer`) in `Editor/TestHttpServer.cs` starts an `HttpListener` on port 8765 via `[InitializeOnLoad]` and routes requests to Unity APIs on the main thread via a `ConcurrentQueue<Action>` + `ManualResetEventSlim` pattern.

## Using the API from this session

If a Unity project with this package installed is open, you can call the server directly:

```bash
# Editor state (isCompiling, compiler errors, play mode)
curl http://localhost:8765/editor/status

# Trigger recompile after writing .cs files
curl -X POST http://localhost:8765/assets/refresh

# Run tests, AI-friendly output
curl -X POST http://localhost:8765/tests/run -H "Content-Type: application/json" -d "{\"minimal\":true}"

# Recent errors
curl "http://localhost:8765/logs?level=error"
```

The full workflow for writing Unity code in an AI loop is documented in `skills/claude-skill.md`. If you are working on a Unity project that uses this package, load that skill.

## File editing rules

- **Do not split `TestHttpServer.cs`** into multiple files. The single-file design is intentional.
- **Do not add NuGet packages.** All JSON serialization is hand-built. The only Unity package dependencies are `UnityEngine.TestRunner` and `UnityEditor.TestRunner`.
- **The `OpenApiSpec` constant** in `TestHttpServer.cs` must stay in sync with the actual routes. Update it whenever you add or change an endpoint.
- **Both skill files** (`skills/claude-skill.md`, `skills/mistral-skill.md`) must be updated whenever the API changes.

## Main thread pattern

Any code touching Unity APIs must run on the main thread. The server runs on a background thread. Use this pattern:

```csharp
static void HandleMyEndpoint(HttpListenerContext ctx)
{
    var ready = new ManualResetEventSlim(false);
    var result = "";

    _mainQueue.Enqueue(() =>
    {
        result = SomeUnityApi.DoSomething();
        ready.Set();
    });

    ready.Wait();
    Respond(ctx, 200, result);
}
```

Endpoints that only read from `ConcurrentQueue` or `ConcurrentDictionary` (like `HandleLogsGet`, `HandleLogsClear`) do not need this pattern.

## Known gotchas

- **`UnityEditor.Compilation.Assembly` has no `compilerMessages` property.** Compiler messages are captured via the `CompilationPipeline.assemblyCompilationFinished` event, stored in `_compilerMessagesByAssembly`, and looked up by assembly name in `HandleEditorStatus`. Do not try to read them from the assembly object directly.
- **`CompilationPipeline.GetAssemblies()` returns `UnityEditor.Compilation.Assembly[]`.** The type must be declared explicitly (`UnityEditor.Compilation.Assembly[]`) rather than `var`, because `System.Reflection.Assembly` is also in scope and `var` resolves to the wrong type.
- **TestRunnerApi must be created and destroyed on the main thread.** It is a `ScriptableObject`. Creation is inside the `_mainQueue` lambda; destruction uses `EditorApplication.delayCall` to defer past the callback.

## Commit style

Conventional commits: `feat:`, `fix:`, `docs:`, `refactor:`. One-line subject, no trailing period. Body only for non-obvious decisions.

## This project was AI-generated

The initial implementation of this package was produced in full by Claude (Anthropic) through a directed conversational session. The human provided requirements and reviewed outputs; Claude wrote all code, the OpenAPI spec, and the skill files. Subsequent changes may be human-written, AI-assisted, or both.
