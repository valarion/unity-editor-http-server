using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

[InitializeOnLoad]
public static class TestHttpServer
{
    // EditorPrefs keys
    const string PrefPort      = "DevTools.HttpServer.Port";
    const string PrefAutoStart = "DevTools.HttpServer.AutoStart";
    public const int DefaultPort = 8765;

    public static int  ConfiguredPort => EditorPrefs.GetInt(PrefPort, DefaultPort);
    public static bool AutoStart      => EditorPrefs.GetBool(PrefAutoStart, true);
    public static bool IsRunning      => _listener?.IsListening == true;

    static HttpListener _listener;
    static Thread _serverThread;
    static readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();

    // Rolling log buffer — populated by Application.logMessageReceived
    const int LogBufferMax = 500;
    static readonly ConcurrentQueue<LogEntry> _logBuffer = new ConcurrentQueue<LogEntry>();

    // Compiler messages captured per assembly via CompilationPipeline.assemblyCompilationFinished
    static readonly ConcurrentDictionary<string, CompilerMessage[]> _compilerMessagesByAssembly =
        new ConcurrentDictionary<string, CompilerMessage[]>(StringComparer.OrdinalIgnoreCase);

    // Custom endpoints registered by other assemblies via RegisterEndpoint()
    static readonly Dictionary<(string method, string path), Action<HttpListenerContext>> _customRoutes =
        new Dictionary<(string, string), Action<HttpListenerContext>>();

    // Asset type aliases for /assets/list?type=X
    static readonly Dictionary<string, string> _typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Script",           "t:MonoScript"       },
        { "Prefab",           "t:Prefab"           },
        { "ScriptableObject", "t:ScriptableObject" },
        { "Scene",            "t:Scene"            },
        { "Texture",          "t:Texture"          },
        { "Material",         "t:Material"         },
        { "AudioClip",        "t:AudioClip"        },
        { "Animation",        "t:AnimationClip"    },
    };

    static TestHttpServer()
    {
        EditorApplication.update += FlushMainQueue;
        EditorApplication.quitting += Shutdown;
        // beforeAssemblyReload fires before every domain reload (recompile).
        // This is the correct hook to release the socket — quitting does not.
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        Application.logMessageReceived += OnLogMessage;
        CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        if (AutoStart) Startup();
    }

    public static void Startup()
    {
        Shutdown();
        var port = ConfiguredPort;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");

        // Retry a few times with a short delay: on Windows, HTTP.sys can take a
        // moment to release a URL registration after Abort() even though the call
        // returned successfully.
        Exception lastEx = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) Thread.Sleep(50);
            try { _listener.Start(); lastEx = null; break; }
            catch (Exception ex) { lastEx = ex; }
        }

        if (lastEx != null)
        {
            Debug.LogError($"[TestHttpServer] Failed to bind port {port}: {lastEx.Message}");
            return;
        }

        _serverThread = new Thread(ServeLoop) { IsBackground = true, Name = "TestHttpServer" };
        _serverThread.Start();
        Debug.Log($"[TestHttpServer] Listening on http://localhost:{port}/  Swagger UI: http://localhost:{port}/swagger");
    }

    public static void Shutdown()
    {
        // Abort() is the forceful path — it immediately invalidates the listener
        // and releases the HTTP.sys URL registration without waiting for in-flight
        // requests to drain (unlike Close/Stop which can leave the port bound).
        try { _listener?.Abort(); } catch { }
        _listener = null;
        _serverThread?.Join(500);
        _serverThread = null;
    }

    public static void SaveConfig(int port, bool autoStart)
    {
        EditorPrefs.SetInt(PrefPort, port);
        EditorPrefs.SetBool(PrefAutoStart, autoStart);
    }

    static void FlushMainQueue()
    {
        while (_mainQueue.TryDequeue(out var action)) action();
    }

    // Called on main thread by Unity for every console message (including compile errors)
    static void OnLogMessage(string message, string stackTrace, LogType type)
    {
        var level = type switch
        {
            LogType.Warning => "warning",
            LogType.Log     => "log",
            _               => "error"   // Error, Exception, Assert
        };
        _logBuffer.Enqueue(new LogEntry
        {
            timestamp  = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            level      = level,
            message    = message,
            stackTrace = string.IsNullOrEmpty(stackTrace) ? null : stackTrace,
        });
        // Keep buffer bounded — drop oldest when over limit
        while (_logBuffer.Count > LogBufferMax)
            _logBuffer.TryDequeue(out _);
    }

    static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
    {
        var key = Path.GetFileNameWithoutExtension(assemblyPath);
        _compilerMessagesByAssembly[key] = messages;
    }

    static void ServeLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Route(ctx));
        }
    }

    // ---------------------------------------------------------------------------
    // Routing
    // ---------------------------------------------------------------------------

    static void Route(HttpListenerContext ctx)
    {
        var path   = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        var method = ctx.Request.HttpMethod.ToUpperInvariant();
        try
        {
            if      (path == "/tests/list"             && method == "GET")  HandleList(ctx);
            else if (path == "/tests/run"              && method == "POST") HandleRun(ctx);
            else if (path == "/editor/status"          && method == "GET")  HandleEditorStatus(ctx);
            else if (path == "/logs"                   && method == "GET")  HandleLogsGet(ctx);
            else if (path == "/logs/clear"             && method == "POST") HandleLogsClear(ctx);
            else if (path == "/assets/refresh"         && method == "POST") HandleAssetsRefresh(ctx);
            else if (path == "/assets/list"            && method == "GET")  HandleAssetsList(ctx);
            else if (path == "/swagger/openapi.json"   && method == "GET")  HandleOpenApiSpec(ctx);
            else if ((path == "/swagger" || path == "/swagger/index.html") && method == "GET") HandleSwaggerUi(ctx);
            else
            {
                Action<HttpListenerContext> custom;
                lock (_customRoutes) _customRoutes.TryGetValue((method, path), out custom);
                if (custom != null) custom(ctx);
                else Respond(ctx, 404, "{\"error\":\"not found\"}");
            }
        }
        catch (Exception ex)
        {
            try { Respond(ctx, 500, $"{{\"error\":{JsonStr(ex.Message)}}}"); }
            catch { }
        }
    }

    // ---------------------------------------------------------------------------
    // Extension API — call these from other assemblies to add custom endpoints
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Register a custom HTTP endpoint. Call from an [InitializeOnLoad] static
    /// constructor in your own assembly. Built-in routes take precedence.
    /// </summary>
    /// <example>
    /// [InitializeOnLoad]
    /// public static class MyEndpoints
    /// {
    ///     static MyEndpoints()
    ///     {
    ///         TestHttpServer.RegisterEndpoint("GET", "/my/data", ctx =>
    ///         {
    ///             var result = TestHttpServer.RunOnMainThread(() => MyApi.GetData());
    ///             TestHttpServer.Respond(ctx, 200, $"{{\"value\":{TestHttpServer.JsonStr(result)}}}");
    ///         });
    ///     }
    /// }
    /// </example>
    public static void RegisterEndpoint(string method, string path, Action<HttpListenerContext> handler)
    {
        var key = (method.ToUpperInvariant(), path.ToLowerInvariant().TrimEnd('/'));
        lock (_customRoutes) _customRoutes[key] = handler;
    }

    /// <summary>
    /// Dispatch work to Unity's main thread and block until it completes.
    /// Use this inside endpoint handlers that need to call Unity APIs.
    /// </summary>
    public static void RunOnMainThread(Action work)
    {
        var ready = new ManualResetEventSlim(false);
        _mainQueue.Enqueue(() => { work(); ready.Set(); });
        ready.Wait();
    }

    /// <summary>
    /// Dispatch work to Unity's main thread, block until it completes, and
    /// return the result.
    /// </summary>
    public static T RunOnMainThread<T>(Func<T> work)
    {
        var ready  = new ManualResetEventSlim(false);
        T   result = default;
        _mainQueue.Enqueue(() => { result = work(); ready.Set(); });
        ready.Wait();
        return result;
    }

    /// <summary>Serialize a string to a JSON string literal, or "null".</summary>
    public static string JsonStr(string s)
    {
        if (s == null) return "null";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                       .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }

    /// <summary>Write an HTTP response. Defaults to application/json.</summary>
    public static void Respond(HttpListenerContext ctx, int status, string body, string contentType = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode      = status;
        ctx.Response.ContentType     = contentType + "; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    // ---------------------------------------------------------------------------
    // GET /tests/list  →  ["Full.Test.Name", ...]
    // ---------------------------------------------------------------------------

    static void HandleList(HttpListenerContext ctx)
    {
        var ready = new ManualResetEventSlim(false);
        var json  = "";

        _mainQueue.Enqueue(() =>
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RetrieveTestList(TestMode.EditMode, root =>
            {
                var names = new List<string>();
                CollectLeaves(root, names);
                json = BuildJsonArray(names);
                EditorApplication.delayCall += () => UnityEngine.Object.DestroyImmediate(api);
                ready.Set();
            });
        });

        ready.Wait();
        Respond(ctx, 200, json);
    }

    // ---------------------------------------------------------------------------
    // POST /tests/run
    //
    // Request body (all fields optional):
    // {
    //   "tests":   ["Pattern*", "Exact.Name"],   // glob wildcards supported; omit = run all
    //   "minimal": true                           // compact AI-friendly summary
    // }
    //
    // Full response:    {"results":[{"name":"...","status":"Passed","duration":0.001,"message":null},...]}
    // Minimal response: {"total":10,"passed":9,"failed":1,"errors":[{"name":"...","message":"...","stackTrace":"..."}]}
    // ---------------------------------------------------------------------------

    static void HandleRun(HttpListenerContext ctx)
    {
        string[] patterns = null;
        bool     minimal  = false;

        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd().Trim();
            if (!string.IsNullOrEmpty(body) && body != "{}")
            {
                patterns = ParseStringArray(body, "tests");
                minimal  = ParseBool(body, "minimal");
            }
        }
        catch { }

        // Resolve wildcard patterns against the full discovered test list.
        string[] resolvedTests = null;
        if (patterns != null && patterns.Length > 0)
        {
            bool hasWildcard = patterns.Any(p => p.Contains('*') || p.Contains('?'));
            if (hasWildcard)
            {
                var allTests = DiscoverTests();
                resolvedTests = allTests
                    .Where(t => patterns.Any(p => GlobMatch(t, p)))
                    .Distinct()
                    .ToArray();
            }
            else
            {
                resolvedTests = patterns;
            }
        }

        var ready   = new ManualResetEventSlim(false);
        var results = new List<TestResultEntry>();

        _mainQueue.Enqueue(() =>
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks(
                onTestFinished: r => { if (!r.Test.IsSuite) results.Add(new TestResultEntry(r)); },
                onRunFinished: _ =>
                {
                    EditorApplication.delayCall += () => UnityEngine.Object.DestroyImmediate(api);
                    ready.Set();
                }
            ));

            var filter = new Filter { testMode = TestMode.EditMode };
            if (resolvedTests != null && resolvedTests.Length > 0)
                filter.testNames = resolvedTests;

            api.Execute(new ExecutionSettings(filter));
        });

        ready.Wait();

        var json = minimal ? BuildMinimalJson(results) : BuildFullResultsJson(results);
        Respond(ctx, 200, json);
    }

    // ---------------------------------------------------------------------------
    // GET /editor/status
    //
    // Returns compilation state, play mode, Unity version, and per-assembly
    // compiler messages. Poll this after POST /assets/refresh until isCompiling
    // is false, then check compilerMessages for errors before running tests.
    // ---------------------------------------------------------------------------

    static void HandleEditorStatus(HttpListenerContext ctx)
    {
        var ready = new ManualResetEventSlim(false);
        var json  = "";

        _mainQueue.Enqueue(() =>
        {
            bool   isCompiling      = EditorApplication.isCompiling;
            bool   isPlaying        = EditorApplication.isPlaying;
            bool   isPaused         = EditorApplication.isPaused;
            double timeSinceStartup = EditorApplication.timeSinceStartup;
            string unityVersion     = Application.unityVersion;
            string productName      = PlayerSettings.productName;

            UnityEditor.Compilation.Assembly[] assemblies = CompilationPipeline.GetAssemblies();
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"isCompiling\":{BoolStr(isCompiling)},");
            sb.Append($"\"isPlaying\":{BoolStr(isPlaying)},");
            sb.Append($"\"isPaused\":{BoolStr(isPaused)},");
            sb.Append($"\"timeSinceStartup\":{timeSinceStartup.ToString("F2", CultureInfo.InvariantCulture)},");
            sb.Append($"\"unityVersion\":{JsonStr(unityVersion)},");
            sb.Append($"\"productName\":{JsonStr(productName)},");
            sb.Append("\"assemblies\":[");

            for (int i = 0; i < assemblies.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var asm  = assemblies[i];
                _compilerMessagesByAssembly.TryGetValue(asm.name, out var msgs);
                if (msgs == null) msgs = Array.Empty<CompilerMessage>();
                sb.Append($"{{\"name\":{JsonStr(asm.name)},\"outputPath\":{JsonStr(asm.outputPath)},\"compilerMessages\":[");
                for (int m = 0; m < msgs.Length; m++)
                {
                    if (m > 0) sb.Append(',');
                    var msg = msgs[m];
                    sb.Append($"{{\"type\":{JsonStr(msg.type.ToString())},\"message\":{JsonStr(msg.message)}," +
                              $"\"file\":{JsonStr(msg.file)},\"line\":{msg.line},\"column\":{msg.column}}}");
                }
                sb.Append("]}");
            }

            sb.Append("]}");
            json = sb.ToString();
            ready.Set();
        });

        ready.Wait();
        Respond(ctx, 200, json);
    }

    // ---------------------------------------------------------------------------
    // GET /logs?level=all|error|warning&limit=N
    //
    // No main-thread dispatch needed — ConcurrentQueue.ToArray() is thread-safe.
    // ---------------------------------------------------------------------------

    static void HandleLogsGet(HttpListenerContext ctx)
    {
        var query       = ctx.Request.QueryString;
        var levelFilter = (query["level"] ?? "all").ToLowerInvariant();
        int limit       = 100;
        if (int.TryParse(query["limit"], out int parsed) && parsed > 0) limit = parsed;

        var all = _logBuffer.ToArray();
        IEnumerable<LogEntry> filtered = levelFilter switch
        {
            "error"   => all.Where(e => e.level == "error"),
            "warning" => all.Where(e => e.level == "warning"),
            _         => all,
        };

        var entries = filtered.Skip(Math.Max(0, filtered.Count() - limit)).ToArray();

        var sb = new StringBuilder($"{{\"count\":{entries.Length},\"entries\":[");
        for (int i = 0; i < entries.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var e = entries[i];
            sb.Append($"{{\"timestamp\":{JsonStr(e.timestamp)},\"level\":{JsonStr(e.level)}," +
                      $"\"message\":{JsonStr(e.message)},\"stackTrace\":{JsonStr(e.stackTrace)}}}");
        }
        sb.Append("]}");
        Respond(ctx, 200, sb.ToString());
    }

    // ---------------------------------------------------------------------------
    // POST /logs/clear  →  { "cleared": N }
    // ---------------------------------------------------------------------------

    static void HandleLogsClear(HttpListenerContext ctx)
    {
        int count = 0;
        while (_logBuffer.TryDequeue(out _)) count++;
        Respond(ctx, 200, $"{{\"cleared\":{count}}}");
    }

    // ---------------------------------------------------------------------------
    // POST /assets/refresh  →  { "refreshed": true, "wasCompiling": false }
    // ---------------------------------------------------------------------------

    static void HandleAssetsRefresh(HttpListenerContext ctx)
    {
        var ready        = new ManualResetEventSlim(false);
        bool wasCompiling = false;

        _mainQueue.Enqueue(() =>
        {
            wasCompiling = EditorApplication.isCompiling;
            AssetDatabase.Refresh();
            ready.Set();
        });

        ready.Wait();
        Respond(ctx, 200, $"{{\"refreshed\":true,\"wasCompiling\":{BoolStr(wasCompiling)}}}");
    }

    // ---------------------------------------------------------------------------
    // GET /assets/list?type=Script|Prefab|ScriptableObject|Scene|Texture|...
    //
    // Returns { "count": N, "assets": [{ "guid": "...", "path": "..." }] }
    // ---------------------------------------------------------------------------

    static void HandleAssetsList(HttpListenerContext ctx)
    {
        var query     = ctx.Request.QueryString;
        var typeParam = query["type"];
        string filter = "";
        if (!string.IsNullOrEmpty(typeParam))
            filter = _typeMap.TryGetValue(typeParam, out var mapped) ? mapped : $"t:{typeParam}";

        var ready = new ManualResetEventSlim(false);
        var json  = "";

        _mainQueue.Enqueue(() =>
        {
            var guids = AssetDatabase.FindAssets(filter);
            var sb    = new StringBuilder($"{{\"count\":{guids.Length},\"assets\":[");
            for (int i = 0; i < guids.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                sb.Append($"{{\"guid\":{JsonStr(guids[i])},\"path\":{JsonStr(path)}}}");
            }
            sb.Append("]}");
            json = sb.ToString();
            ready.Set();
        });

        ready.Wait();
        Respond(ctx, 200, json);
    }

    // ---------------------------------------------------------------------------
    // GET /swagger/openapi.json  and  GET /swagger
    // ---------------------------------------------------------------------------

    static void HandleOpenApiSpec(HttpListenerContext ctx) => Respond(ctx, 200, OpenApiSpec, "application/json");
    static void HandleSwaggerUi(HttpListenerContext ctx)   => Respond(ctx, 200, SwaggerUiHtml, "text/html");

    // ---------------------------------------------------------------------------
    // Test discovery helper (used for wildcard expansion in /tests/run)
    // ---------------------------------------------------------------------------

    static List<string> DiscoverTests()
    {
        var ready = new ManualResetEventSlim(false);
        var names = new List<string>();

        _mainQueue.Enqueue(() =>
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RetrieveTestList(TestMode.EditMode, root =>
            {
                CollectLeaves(root, names);
                EditorApplication.delayCall += () => UnityEngine.Object.DestroyImmediate(api);
                ready.Set();
            });
        });

        ready.Wait();
        return names;
    }

    static void CollectLeaves(ITestAdaptor node, List<string> results)
    {
        if (!node.IsSuite) { results.Add(node.FullName); return; }
        foreach (var child in node.Children) CollectLeaves(child, results);
    }

    static bool GlobMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // JSON builders
    // ---------------------------------------------------------------------------

    static string BuildFullResultsJson(List<TestResultEntry> results)
    {
        var sb = new StringBuilder("{\"results\":[");
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = results[i];
            sb.Append($"{{\"name\":{JsonStr(r.Name)},\"status\":{JsonStr(r.Status)}," +
                      $"\"duration\":{r.Duration.ToString("F4", CultureInfo.InvariantCulture)}," +
                      $"\"message\":{JsonStr(r.Message)}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string BuildMinimalJson(List<TestResultEntry> results)
    {
        int total  = results.Count;
        int passed = results.Count(r => r.Status == "Passed");
        int failed = total - passed;
        var errors = results.Where(r => r.Status != "Passed").ToList();

        var sb = new StringBuilder($"{{\"total\":{total},\"passed\":{passed},\"failed\":{failed},\"errors\":[");
        for (int i = 0; i < errors.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"name\":{JsonStr(errors[i].Name)},\"message\":{JsonStr(errors[i].Message)}," +
                      $"\"stackTrace\":{JsonStr(errors[i].StackTrace)}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string BuildJsonArray(List<string> items)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonStr(items[i]));
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------
    // Minimal JSON parsers (no external dependencies)
    // ---------------------------------------------------------------------------

    static string[] ParseStringArray(string json, string key)
    {
        var keyIndex = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        var start = json.IndexOf('[', keyIndex);
        var end   = json.IndexOf(']', keyIndex);
        if (start < 0 || end <= start) return null;
        var inner = json.Substring(start + 1, end - start - 1).Trim();
        if (string.IsNullOrEmpty(inner)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var part in inner.Split(','))
        {
            var s = part.Trim().Trim('"');
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list.ToArray();
    }

    static bool ParseBool(string json, string key)
    {
        var m = Regex.Match(json, $@"""{key}""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.ToLowerInvariant() == "true";
    }

    static string BoolStr(bool v) => v ? "true" : "false";

    // ---------------------------------------------------------------------------
    // Inner types
    // ---------------------------------------------------------------------------

    struct LogEntry { public string timestamp, level, message, stackTrace; }

    struct TestResultEntry
    {
        public string Name, Status, Message, StackTrace;
        public double Duration;

        public TestResultEntry(ITestResultAdaptor r)
        {
            Name       = r.Test.FullName;
            Status     = r.TestStatus.ToString();
            Duration   = r.Duration;
            Message    = string.IsNullOrEmpty(r.Message)    ? null : r.Message;
            StackTrace = string.IsNullOrEmpty(r.StackTrace) ? null : r.StackTrace;
        }
    }

    class Callbacks : ICallbacks
    {
        readonly Action<ITestResultAdaptor> _onTestFinished, _onRunFinished;
        public Callbacks(Action<ITestResultAdaptor> onTestFinished, Action<ITestResultAdaptor> onRunFinished)
            { _onTestFinished = onTestFinished; _onRunFinished = onRunFinished; }
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void RunFinished(ITestResultAdaptor result) => _onRunFinished(result);
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) => _onTestFinished(result);
    }

    // ---------------------------------------------------------------------------
    // OpenAPI 3.0 spec
    // ---------------------------------------------------------------------------

    const string OpenApiSpec = @"{
  ""openapi"": ""3.0.3"",
  ""info"": {
    ""title"": ""Unity Editor HTTP Server"",
    ""description"": ""Programmatic access to Unity editor state, tests, logs, and assets. Designed for AI agents operating in a write-compile-test loop."",
    ""version"": ""1.0.0""
  },
  ""servers"": [{""url"": ""http://localhost:8765""}],
  ""paths"": {
    ""/editor/status"": {
      ""get"": {
        ""summary"": ""Unity editor state"",
        ""description"": ""Returns compilation state, play mode, Unity version, and per-assembly compiler messages. Poll after POST /assets/refresh until isCompiling is false, then check compilerMessages for errors."",
        ""responses"": {
          ""200"": {
            ""description"": ""Editor status"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/EditorStatus"" } } }
          }
        }
      }
    },
    ""/logs"": {
      ""get"": {
        ""summary"": ""Recent console log entries"",
        ""description"": ""Rolling buffer of the last 500 console messages, including compile errors. Use level=error to focus on failures."",
        ""parameters"": [
          { ""name"": ""level"", ""in"": ""query"", ""schema"": { ""type"": ""string"", ""enum"": [""all"",""error"",""warning""], ""default"": ""all"" } },
          { ""name"": ""limit"", ""in"": ""query"", ""schema"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1 } }
        ],
        ""responses"": {
          ""200"": {
            ""description"": ""Log entries"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/LogResponse"" } } }
          }
        }
      }
    },
    ""/logs/clear"": {
      ""post"": {
        ""summary"": ""Clear log buffer"",
        ""responses"": {
          ""200"": {
            ""description"": ""Number of entries cleared"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/ClearResponse"" } } }
          }
        }
      }
    },
    ""/assets/refresh"": {
      ""post"": {
        ""summary"": ""Trigger AssetDatabase.Refresh()"",
        ""description"": ""Dispatches AssetDatabase.Refresh() on the main thread. Call this after writing files to disk. Then poll /editor/status until isCompiling is false."",
        ""responses"": {
          ""200"": {
            ""description"": ""Refresh dispatched"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/RefreshResponse"" } } }
          }
        }
      }
    },
    ""/assets/list"": {
      ""get"": {
        ""summary"": ""Enumerate project assets"",
        ""parameters"": [
          {
            ""name"": ""type"", ""in"": ""query"",
            ""description"": ""Asset type: Script, Prefab, ScriptableObject, Scene, Texture, Material, AudioClip, Animation. Omit for all."",
            ""schema"": { ""type"": ""string"" }
          }
        ],
        ""responses"": {
          ""200"": {
            ""description"": ""Asset GUIDs and paths"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/AssetListResponse"" } } }
          }
        }
      }
    },
    ""/tests/list"": {
      ""get"": {
        ""summary"": ""List all edit-mode tests"",
        ""description"": ""Returns the fully-qualified name of every test discovered in edit-mode assemblies."",
        ""responses"": {
          ""200"": {
            ""description"": ""Array of test full names"",
            ""content"": {
              ""application/json"": {
                ""schema"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
              }
            }
          }
        }
      }
    },
    ""/tests/run"": {
      ""post"": {
        ""summary"": ""Run edit-mode tests"",
        ""description"": ""Executes tests and returns results. Supports glob wildcards (* and ?) in test names. Use minimal:true for AI-friendly compact output."",
        ""requestBody"": {
          ""content"": {
            ""application/json"": {
              ""schema"": { ""$ref"": ""#/components/schemas/RunRequest"" },
              ""examples"": {
                ""run_all"":      { ""summary"": ""Run all tests"",            ""value"": {} },
                ""run_exact"":    { ""summary"": ""Run by exact name"",        ""value"": { ""tests"": [""My.Namespace.MyTest""] } },
                ""run_wildcard"": { ""summary"": ""Run by wildcard"",          ""value"": { ""tests"": [""*MyService*""] } },
                ""run_minimal"":  { ""summary"": ""Run all, minimal output"",  ""value"": { ""minimal"": true } }
              }
            }
          }
        },
        ""responses"": {
          ""200"": {
            ""description"": ""Test results — full or minimal depending on the minimal flag."",
            ""content"": {
              ""application/json"": {
                ""schema"": { ""oneOf"": [ { ""$ref"": ""#/components/schemas/FullResults"" }, { ""$ref"": ""#/components/schemas/MinimalResults"" } ] }
              }
            }
          }
        }
      }
    }
  },
  ""components"": {
    ""schemas"": {
      ""EditorStatus"": {
        ""type"": ""object"",
        ""properties"": {
          ""isCompiling"":      { ""type"": ""boolean"" },
          ""isPlaying"":        { ""type"": ""boolean"" },
          ""isPaused"":         { ""type"": ""boolean"" },
          ""timeSinceStartup"": { ""type"": ""number"" },
          ""unityVersion"":     { ""type"": ""string"" },
          ""productName"":      { ""type"": ""string"" },
          ""assemblies"":       { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/AssemblyInfo"" } }
        }
      },
      ""AssemblyInfo"": {
        ""type"": ""object"",
        ""properties"": {
          ""name"":             { ""type"": ""string"" },
          ""outputPath"":       { ""type"": ""string"" },
          ""compilerMessages"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/CompilerMessage"" } }
        }
      },
      ""CompilerMessage"": {
        ""type"": ""object"",
        ""properties"": {
          ""type"":    { ""type"": ""string"", ""enum"": [""Error"",""Warning"",""Information""] },
          ""message"": { ""type"": ""string"" },
          ""file"":    { ""type"": ""string"" },
          ""line"":    { ""type"": ""integer"" },
          ""column"":  { ""type"": ""integer"" }
        }
      },
      ""LogResponse"": {
        ""type"": ""object"",
        ""properties"": {
          ""count"":   { ""type"": ""integer"" },
          ""entries"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/LogEntry"" } }
        }
      },
      ""LogEntry"": {
        ""type"": ""object"",
        ""properties"": {
          ""timestamp"":  { ""type"": ""string"", ""format"": ""date-time"" },
          ""level"":      { ""type"": ""string"", ""enum"": [""log"",""warning"",""error""] },
          ""message"":    { ""type"": ""string"" },
          ""stackTrace"": { ""type"": ""string"", ""nullable"": true }
        }
      },
      ""ClearResponse"": {
        ""type"": ""object"",
        ""properties"": { ""cleared"": { ""type"": ""integer"" } }
      },
      ""RefreshResponse"": {
        ""type"": ""object"",
        ""properties"": {
          ""refreshed"":    { ""type"": ""boolean"" },
          ""wasCompiling"": { ""type"": ""boolean"", ""description"": ""True if Unity was already compiling when refresh was requested."" }
        }
      },
      ""AssetListResponse"": {
        ""type"": ""object"",
        ""properties"": {
          ""count"":  { ""type"": ""integer"" },
          ""assets"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/AssetEntry"" } }
        }
      },
      ""AssetEntry"": {
        ""type"": ""object"",
        ""properties"": {
          ""guid"": { ""type"": ""string"" },
          ""path"": { ""type"": ""string"" }
        }
      },
      ""RunRequest"": {
        ""type"": ""object"",
        ""properties"": {
          ""tests"":   { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Test names or glob patterns (* and ?). Omit to run all."" },
          ""minimal"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Compact summary instead of per-test details. Recommended for AI consumers."" }
        }
      },
      ""FullResults"": {
        ""type"": ""object"",
        ""properties"": {
          ""results"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/TestResult"" } }
        }
      },
      ""TestResult"": {
        ""type"": ""object"",
        ""properties"": {
          ""name"":     { ""type"": ""string"" },
          ""status"":   { ""type"": ""string"", ""enum"": [""Passed"",""Failed"",""Skipped"",""Inconclusive""] },
          ""duration"": { ""type"": ""number"", ""format"": ""double"", ""description"": ""Seconds"" },
          ""message"":  { ""type"": ""string"", ""nullable"": true }
        }
      },
      ""MinimalResults"": {
        ""type"": ""object"",
        ""properties"": {
          ""total"":  { ""type"": ""integer"" },
          ""passed"": { ""type"": ""integer"" },
          ""failed"": { ""type"": ""integer"" },
          ""errors"": { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/TestError"" }, ""description"": ""Only non-passing tests."" }
        }
      },
      ""TestError"": {
        ""type"": ""object"",
        ""properties"": {
          ""name"":       { ""type"": ""string"" },
          ""message"":    { ""type"": ""string"" },
          ""stackTrace"": { ""type"": ""string"", ""nullable"": true }
        }
      }
    }
  }
}";

    // ---------------------------------------------------------------------------
    // Swagger UI HTML
    // ---------------------------------------------------------------------------

    const string SwaggerUiHtml = @"<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8""/>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Unity Editor HTTP Server – API</title>
  <link rel=""stylesheet"" href=""https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"">
</head>
<body>
<div id=""swagger-ui""></div>
<script src=""https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js""></script>
<script>
  SwaggerUIBundle({
    url: '/swagger/openapi.json',
    dom_id: '#swagger-ui',
    deepLinking: true,
    presets: [SwaggerUIBundle.presets.apis, SwaggerUIBundle.SwaggerUIStandalonePreset],
    layout: 'BaseLayout',
    tryItOutEnabled: true,
    requestInterceptor: req => { req.headers['Accept'] = 'application/json'; return req; }
  });
</script>
</body>
</html>";
}

// =============================================================================
// Settings window  —  Tools > HTTP Server > Settings
// =============================================================================

public class HttpServerSettingsWindow : EditorWindow
{
    int  _port;
    bool _autoStart;
    bool _dirty;

    [MenuItem("Tools/HTTP Server/Settings")]
    public static void Open() => GetWindow<HttpServerSettingsWindow>("HTTP Server").minSize = new UnityEngine.Vector2(320, 200);

    void OnEnable()
    {
        _port      = TestHttpServer.ConfiguredPort;
        _autoStart = TestHttpServer.AutoStart;
        _dirty     = false;
    }

    void OnGUI()
    {
        EditorGUILayout.Space(8);

        // Status
        var statusStyle = new GUIStyle(EditorStyles.boldLabel);
        statusStyle.normal.textColor = TestHttpServer.IsRunning
            ? new UnityEngine.Color(0.2f, 0.8f, 0.2f)
            : new UnityEngine.Color(0.8f, 0.3f, 0.3f);
        EditorGUILayout.LabelField("Status", TestHttpServer.IsRunning
            ? $"Running on port {TestHttpServer.ConfiguredPort}"
            : "Stopped", statusStyle);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();

        var newPort = EditorGUILayout.IntField("Port", _port);
        if (newPort != _port) { _port = UnityEngine.Mathf.Clamp(newPort, 1024, 65535); _dirty = true; }

        var newAuto = EditorGUILayout.Toggle("Auto-start on load", _autoStart);
        if (newAuto != _autoStart) { _autoStart = newAuto; _dirty = true; }

        EditorGUILayout.Space(12);
        EditorGUILayout.BeginHorizontal();

        GUI.enabled = _dirty;
        if (GUILayout.Button("Apply & Restart"))
        {
            TestHttpServer.SaveConfig(_port, _autoStart);
            _dirty = false;
            // Defer out of OnGUI — socket operations must not block the GUI event loop.
            EditorApplication.delayCall += () => { TestHttpServer.Startup(); Repaint(); };
        }
        GUI.enabled = true;

        if (TestHttpServer.IsRunning)
        {
            if (GUILayout.Button("Stop"))
                EditorApplication.delayCall += () => { TestHttpServer.Shutdown(); Repaint(); };
        }
        else
        {
            if (GUILayout.Button("Start"))
                EditorApplication.delayCall += () => { TestHttpServer.Startup(); Repaint(); };
        }

        EditorGUILayout.EndHorizontal();

        if (_dirty)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("Unsaved changes. Click Apply & Restart to use the new port.", MessageType.Info);
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Swagger UI", TestHttpServer.IsRunning
            ? $"http://localhost:{TestHttpServer.ConfiguredPort}/swagger"
            : "—", EditorStyles.miniLabel);
    }
}
