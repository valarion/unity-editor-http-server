using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    public static bool IsRunning      => _serverSocket != null && !_paused;

    static Socket            _serverSocket;
    static Thread            _serverThread;
    static volatile bool     _paused;          // true = accept connections but return 503, socket stays open
    static int               _activePort;      // port actually bound; set on main thread in Startup()
    static readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();

    // Rolling log buffer — populated by Application.logMessageReceived
    const int LogBufferMax = 500;
    static readonly ConcurrentQueue<LogEntry> _logBuffer = new ConcurrentQueue<LogEntry>();

    // Compiler messages captured per assembly via CompilationPipeline.assemblyCompilationFinished
    static readonly ConcurrentDictionary<string, CompilerMessage[]> _compilerMessagesByAssembly =
        new ConcurrentDictionary<string, CompilerMessage[]>(StringComparer.OrdinalIgnoreCase);

    // Custom endpoints registered by other assemblies via RegisterEndpoint()
    static readonly Dictionary<(string method, string path), Action<ServerHttpContext>> _customRoutes =
        new Dictionary<(string, string), Action<ServerHttpContext>>();

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

    // Full init: tears down any existing socket and creates a fresh one with SO_REUSEADDR.
    // SO_REUSEADDR lets us rebind the same port immediately even if prior connections are
    // still in TCP TIME_WAIT — this is what makes port changes (including reverts) reliable.
    public static void Startup()
    {
        Shutdown();
        _paused = false;
        var port = ConfiguredPort;   // EditorPrefs — must be read on main thread (here in Startup)
        _activePort = port;
        try
        {
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            _serverSocket.Listen(32);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TestHttpServer] Failed to bind port {port}: {ex.Message}");
            try { _serverSocket?.Close(); } catch { }
            _serverSocket = null;
            return;
        }
        _serverThread = new Thread(ServeLoop) { IsBackground = true, Name = "TestHttpServer" };
        _serverThread.Start();
        Debug.Log($"[TestHttpServer] Listening on http://localhost:{port}/  Swagger UI: http://localhost:{port}/swagger");
    }

    // Pause: the socket stays open; every accepted connection gets a 503.
    public static void Pause()
    {
        _paused = true;
        Debug.Log("[TestHttpServer] Paused (socket still bound, returning 503).");
    }

    // Resume a paused server. If the socket is gone, falls back to a full Startup().
    public static void Resume()
    {
        if (_serverSocket == null) { Startup(); return; }
        _paused = false;
        Debug.Log($"[TestHttpServer] Resumed on http://localhost:{ConfiguredPort}/");
    }

    // Full teardown: closes the socket. SO_REUSEADDR on the next Startup() means the
    // same port (or a reverted port) can be rebound immediately regardless of TIME_WAIT.
    public static void Shutdown()
    {
        _paused = false;
        try { _serverSocket?.Close(); } catch { }
        _serverSocket = null;
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
        while (_serverSocket != null)
        {
            Socket client;
            try { client = _serverSocket.Accept(); }
            catch { break; }
            var captured = client;
            ThreadPool.QueueUserWorkItem(_ => HandleClient(captured));
        }
    }

    static void HandleClient(Socket client)
    {
        try
        {
            using var ns = new NetworkStream(client, ownsSocket: true);

            // Read until the blank line that ends HTTP headers (\r\n\r\n).
            var headerBuf = new List<byte>(512);
            int b;
            while ((b = ns.ReadByte()) >= 0)
            {
                headerBuf.Add((byte)b);
                int n = headerBuf.Count;
                if (n >= 4 &&
                    headerBuf[n - 4] == '\r' && headerBuf[n - 3] == '\n' &&
                    headerBuf[n - 2] == '\r' && headerBuf[n - 1] == '\n')
                    break;
                if (n > 8192) return;   // header too large — drop
            }
            if (b < 0) return;

            var lines = Encoding.ASCII.GetString(headerBuf.ToArray())
                .Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0].ToUpperInvariant();
            var rawUrl  = parts[1];

            var hdrMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                    hdrMap[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            // Parse URL + query string.
            Uri uri;
            try { uri = new Uri("http://localhost" + rawUrl); }
            catch { return; }

            var qs = new System.Collections.Specialized.NameValueCollection();
            if (!string.IsNullOrEmpty(uri.Query))
            {
                foreach (var param in uri.Query.TrimStart('?').Split('&'))
                {
                    if (string.IsNullOrEmpty(param)) continue;
                    var eq = param.IndexOf('=');
                    if (eq > 0)
                        qs[Uri.UnescapeDataString(param.Substring(0, eq))] =
                            Uri.UnescapeDataString(param.Substring(eq + 1));
                    else
                        qs[Uri.UnescapeDataString(param)] = "";
                }
            }

            // Read body (if Content-Length is present).
            Stream bodyStream = Stream.Null;
            if (hdrMap.TryGetValue("Content-Length", out var clStr) &&
                int.TryParse(clStr, out var cl) && cl > 0)
            {
                var buf = new byte[cl];
                int read = 0;
                while (read < cl) { int r = ns.Read(buf, read, cl - read); if (r <= 0) break; read += r; }
                bodyStream = new MemoryStream(buf, 0, read, writable: false);
            }

            var ctx = new ServerHttpContext
            {
                Request = new ServerHttpRequest
                {
                    Method       = method,
                    AbsolutePath = uri.AbsolutePath,
                    QueryString  = qs,
                    InputStream  = bodyStream,
                },
                Response = new ServerHttpResponse(ns),
            };

            if (_paused) Respond(ctx, 503, "{\"error\":\"server paused\"}");
            else         Route(ctx);
        }
        catch { }
    }

    // ---------------------------------------------------------------------------
    // Routing
    // ---------------------------------------------------------------------------

    static void Route(ServerHttpContext ctx)
    {
        var path   = ctx.Request.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        var method = ctx.Request.Method;
        try
        {
            if      (path == "/tests/list"             && method == "GET")  HandleList(ctx);
            else if (path == "/tests/run"              && method == "POST") HandleRun(ctx);
            else if (path == "/editor/status"          && method == "GET")  HandleEditorStatus(ctx);
            else if (path == "/logs"                   && method == "GET")  HandleLogsGet(ctx);
            else if (path == "/logs/clear"             && method == "POST") HandleLogsClear(ctx);
            else if (path == "/assets/refresh"         && method == "POST") HandleAssetsRefresh(ctx);
            else if (path == "/assets/list"            && method == "GET")  HandleAssetsList(ctx);
            else if (path == "/editor/selection"       && method == "GET")  HandleEditorSelection(ctx);
            else if (path == "/editor/screenshot"      && method == "GET")  HandleScreenshot(ctx);
            else if (path == "/swagger/openapi.json"   && method == "GET")  HandleOpenApiSpec(ctx);
            else if ((path == "/swagger" || path == "/swagger/index.html") && method == "GET") HandleSwaggerUi(ctx);
            else
            {
                Action<ServerHttpContext> custom;
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
    public static void RegisterEndpoint(string method, string path, Action<ServerHttpContext> handler)
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
    public static void Respond(ServerHttpContext ctx, int status, string body, string contentType = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    /// <summary>Write a raw binary HTTP response (e.g. image/png).</summary>
    public static void RespondBytes(ServerHttpContext ctx, int status, byte[] body, string contentType)
    {
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }

    // ---------------------------------------------------------------------------
    // GET /tests/list  →  ["Full.Test.Name", ...]
    // ---------------------------------------------------------------------------

    static void HandleList(ServerHttpContext ctx)
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

    static void HandleRun(ServerHttpContext ctx)
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

    static void HandleEditorStatus(ServerHttpContext ctx)
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

    static void HandleLogsGet(ServerHttpContext ctx)
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

    static void HandleLogsClear(ServerHttpContext ctx)
    {
        int count = 0;
        while (_logBuffer.TryDequeue(out _)) count++;
        Respond(ctx, 200, $"{{\"cleared\":{count}}}");
    }

    // ---------------------------------------------------------------------------
    // POST /assets/refresh  →  { "refreshed": true, "wasCompiling": false }
    // ---------------------------------------------------------------------------

    static void HandleAssetsRefresh(ServerHttpContext ctx)
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
    // GET /editor/screenshot?superSize=1
    //
    // Captures the Game view as a PNG and returns the raw image bytes
    // (Content-Type: image/png).  Use superSize=2/4 for higher-resolution captures.
    //
    // The Game view must be open and have rendered at least one frame; if it has
    // not, Unity returns null and the endpoint responds with 503.
    //
    // Use cases: verify UI layout, check for visual artifacts, confirm render state.
    // ---------------------------------------------------------------------------

    static void HandleScreenshot(ServerHttpContext ctx)
    {
        int superSize = 1;
        if (int.TryParse(ctx.Request.QueryString["superSize"], out var ss) && ss >= 1 && ss <= 8)
            superSize = ss;

        var ready    = new ManualResetEventSlim(false);
        byte[] png   = null;
        string error = null;

        // Enqueue to main thread only to register the delayCall — the actual capture
        // must NOT run during EditorApplication.update (FlushMainQueue) because that
        // fires mid-frame while URP render passes are active, causing attachment
        // dimension-mismatch errors. delayCall fires on the NEXT editor tick, after the
        // current frame's rendering has fully completed.
        _mainQueue.Enqueue(() =>
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var tex = ScreenCapture.CaptureScreenshotAsTexture(superSize);
                    if (tex != null)
                    {
                        png = tex.EncodeToPNG();
                        UnityEngine.Object.DestroyImmediate(tex);
                    }
                    else
                    {
                        error = "{\"error\":\"capture returned null — open the Game view and ensure at least one frame has rendered\"}";
                    }
                }
                catch (Exception ex)
                {
                    error = $"{{\"error\":{JsonStr(ex.Message)}}}";
                }
                ready.Set();
            };
        });

        ready.Wait();

        if (error != null) Respond(ctx, 503, error);
        else               RespondBytes(ctx, 200, png, "image/png");
    }

    // GET /editor/selection
    //
    // Returns the currently selected objects in the Unity Editor.
    // Uses Selection.entityIds (Unity 6.5+) to enumerate all selected objects.
    //
    // Response:
    // {
    //   "count": 2,
    //   "activeObject": { "name": "MyScript", "type": "MonoScript", "assetPath": "Assets/Scripts/MyScript.cs" },
    //   "objects": [
    //     { "name": "MyScript", "type": "MonoScript", "assetPath": "Assets/Scripts/MyScript.cs" },
    //     { "name": "Player",   "type": "GameObject", "assetPath": null }
    //   ]
    // }
    // assetPath is null for scene objects (not assets on disk).
    // ---------------------------------------------------------------------------

    static void HandleEditorSelection(ServerHttpContext ctx)
    {
        var ready = new ManualResetEventSlim(false);
        var json  = "";

        _mainQueue.Enqueue(() =>
        {
            var ids       = Selection.entityIds;            // EntityId[] — Unity 6.5+
            var activeId  = Selection.activeEntityId;       // EntityId   — Unity 6.5+

            string SerializeObject(EntityId id)
            {
                var obj = EditorUtility.EntityIdToObject(id);
                if (obj == null) return null;
                var assetPath = AssetDatabase.GetAssetPath(id);
                return $"{{\"name\":{JsonStr(obj.name)},\"type\":{JsonStr(obj.GetType().Name)}," +
                       $"\"assetPath\":{JsonStr(string.IsNullOrEmpty(assetPath) ? null : assetPath)}}}";
            }

            var activeJson = activeId.IsValid() ? SerializeObject(activeId) : "null";

            var sb = new StringBuilder($"{{\"count\":{ids.Length},\"activeObject\":{activeJson},\"objects\":[");
            bool first = true;
            foreach (var id in ids)
            {
                var entry = SerializeObject(id);
                if (entry == null) continue;
                if (!first) sb.Append(',');
                sb.Append(entry);
                first = false;
            }
            sb.Append("]}");
            json = sb.ToString();
            ready.Set();
        });

        ready.Wait();
        Respond(ctx, 200, json);
    }

    // GET /assets/list?type=Script|Prefab|ScriptableObject|Scene|Texture|...
    //
    // Returns { "count": N, "assets": [{ "guid": "...", "path": "..." }] }
    // ---------------------------------------------------------------------------

    static void HandleAssetsList(ServerHttpContext ctx)
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

    static void HandleOpenApiSpec(ServerHttpContext ctx) =>
        Respond(ctx, 200, OpenApiSpec.Replace("\"http://localhost:8765\"",
            $"\"http://localhost:{_activePort}\""), "application/json");
    static void HandleSwaggerUi(ServerHttpContext ctx)   => Respond(ctx, 200, SwaggerUiHtml, "text/html");

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
    ""/editor/screenshot"": {
      ""get"": {
        ""summary"": ""Capture the Game view as PNG"",
        ""description"": ""Returns a raw PNG screenshot of the Unity Game view. The Game view must be open and have rendered at least one frame. Use superSize for higher-resolution captures (2× or 4× the display resolution)."",
        ""parameters"": [
          {
            ""name"": ""superSize"", ""in"": ""query"",
            ""description"": ""Resolution multiplier (1–8). Default 1. 2 doubles width and height."",
            ""schema"": { ""type"": ""integer"", ""default"": 1, ""minimum"": 1, ""maximum"": 8 }
          }
        ],
        ""responses"": {
          ""200"": {
            ""description"": ""PNG image bytes"",
            ""content"": { ""image/png"": { ""schema"": { ""type"": ""string"", ""format"": ""binary"" } } }
          },
          ""503"": {
            ""description"": ""Game view not available or no frame rendered yet"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/ErrorResponse"" } } }
          }
        }
      }
    },
    ""/editor/selection"": {
      ""get"": {
        ""summary"": ""Get current Editor selection"",
        ""description"": ""Returns the objects currently selected in the Unity Editor using the Unity 6.5 EntityId API. assetPath is null for scene objects."",
        ""responses"": {
          ""200"": {
            ""description"": ""Selected objects"",
            ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/SelectionResponse"" } } }
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
      ""ErrorResponse"": {
        ""type"": ""object"",
        ""properties"": { ""error"": { ""type"": ""string"" } }
      },
      ""SelectionResponse"": {
        ""type"": ""object"",
        ""properties"": {
          ""count"":        { ""type"": ""integer"" },
          ""activeObject"": { ""$ref"": ""#/components/schemas/SelectedObject"", ""nullable"": true },
          ""objects"":      { ""type"": ""array"", ""items"": { ""$ref"": ""#/components/schemas/SelectedObject"" } }
        }
      },
      ""SelectedObject"": {
        ""type"": ""object"",
        ""properties"": {
          ""name"":      { ""type"": ""string"" },
          ""type"":      { ""type"": ""string"", ""description"": ""C# type name, e.g. GameObject, MonoScript, Texture2D"" },
          ""assetPath"": { ""type"": ""string"", ""nullable"": true, ""description"": ""Asset path if the object is a project asset; null for scene objects"" }
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

    // Cached styles — created once in OnEnable, not every frame.
    GUIStyle _runningStyle;
    GUIStyle _stoppedStyle;

    [MenuItem("Tools/HTTP Server/Settings")]
    public static void Open() => GetWindow<HttpServerSettingsWindow>("HTTP Server").minSize = new UnityEngine.Vector2(320, 200);

    void OnEnable()
    {
        _port      = TestHttpServer.ConfiguredPort;
        _autoStart = TestHttpServer.AutoStart;
        _dirty     = false;
        _runningStyle = null; // rebuilt on first OnGUI once EditorStyles is ready
        _stoppedStyle = null;
    }

    void OnGUI()
    {
        // Build styles lazily — EditorStyles is not guaranteed to be ready in OnEnable.
        if (_runningStyle == null)
        {
            _runningStyle = new GUIStyle(EditorStyles.boldLabel);
            _runningStyle.normal.textColor = new UnityEngine.Color(0.2f, 0.8f, 0.2f);
            _stoppedStyle = new GUIStyle(EditorStyles.boldLabel);
            _stoppedStyle.normal.textColor = new UnityEngine.Color(0.8f, 0.3f, 0.3f);
        }

        EditorGUILayout.Space(8);

        bool running = TestHttpServer.IsRunning;
        EditorGUILayout.LabelField("Status",
            running ? $"Running on port {TestHttpServer.ConfiguredPort}" : "Stopped",
            running ? _runningStyle : _stoppedStyle);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);

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
            // Full restart needed — port may have changed, so Shutdown + Startup.
            EditorApplication.delayCall += () => { TestHttpServer.Startup(); Repaint(); };
        }
        GUI.enabled = true;

        if (running)
        {
            // Pause keeps the socket reserved — Resume() can rebind without OS timing issues.
            if (GUILayout.Button("Stop"))
                EditorApplication.delayCall += () => { TestHttpServer.Pause(); Repaint(); };
        }
        else
        {
            if (GUILayout.Button("Start"))
                EditorApplication.delayCall += () => { TestHttpServer.Resume(); Repaint(); };
        }

        EditorGUILayout.EndHorizontal();

        if (_dirty)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("Unsaved changes — click Apply & Restart to use the new port.", MessageType.Info);
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Swagger UI",
            running ? $"http://localhost:{TestHttpServer.ConfiguredPort}/swagger" : "—",
            EditorStyles.miniLabel);
    }
}

// =============================================================================
// HTTP context types — used by built-in handlers and the RegisterEndpoint() API
// =============================================================================

public class ServerHttpRequest
{
    public string Method       { get; internal set; }
    public string AbsolutePath { get; internal set; }
    public System.Collections.Specialized.NameValueCollection QueryString { get; internal set; }
        = new System.Collections.Specialized.NameValueCollection();
    public Stream InputStream  { get; internal set; } = Stream.Null;
}

public class ServerHttpResponse
{
    readonly NetworkStream _stream;
    readonly MemoryStream  _body   = new MemoryStream();
    bool _closed;

    internal ServerHttpResponse(NetworkStream stream) => _stream = stream;

    public int    StatusCode      { get; set; } = 200;
    public string ContentType     { get; set; } = "application/json";
    public long   ContentLength64 { get; set; }  // ignored — computed from body at Close()
    public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();
    public Stream OutputStream => _body;

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            var body = _body.ToArray();
            var statusText = StatusCode switch
            {
                200 => "OK",  201 => "Created",  400 => "Bad Request",
                404 => "Not Found",  500 => "Internal Server Error",
                503 => "Service Unavailable",  _   => "Unknown"
            };
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {StatusCode} {statusText}\r\n");
            sb.Append($"Content-Type: {ContentType}; charset=utf-8\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
            foreach (var kv in Headers)
                sb.Append($"{kv.Key}: {kv.Value}\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            _stream.Write(headerBytes, 0, headerBytes.Length);
            _stream.Write(body, 0, body.Length);
            _stream.Flush();
        }
        catch { }
        finally { try { _stream.Close(); } catch { } }
    }
}

public class ServerHttpContext
{
    public ServerHttpRequest  Request  { get; internal set; }
    public ServerHttpResponse Response { get; internal set; }
}

// =============================================================================
// Console double-click handler
// When the server is running, double-clicking any TestHttpServer log entry
// opens the Swagger UI in the browser instead of opening the script in the editor.
// =============================================================================

public class HttpServerConsoleLink
{
    // Cached instanceID of TestHttpServer.cs — resolved lazily on first double-click
    // using only non-deprecated AssetDatabase APIs (FindAssets + LoadAssetAtPath).
    static EntityId _scriptInstanceId;

    static EntityId ScriptInstanceId()
    {
        if (_scriptInstanceId.IsValid()) return _scriptInstanceId;
        foreach (var guid in AssetDatabase.FindAssets("TestHttpServer t:MonoScript"))
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(
                AssetDatabase.GUIDToAssetPath(guid));
            if (script != null) { _scriptInstanceId = script.GetEntityId(); break; }
        }
        return _scriptInstanceId;
    }

    [UnityEditor.Callbacks.OnOpenAsset]
    static bool OnOpenAsset(EntityId instanceID, int line)
    {
        if (!TestHttpServer.IsRunning) return false;
        if (instanceID != ScriptInstanceId()) return false;
        Application.OpenURL($"http://localhost:{TestHttpServer.ConfiguredPort}/swagger");
        return true;   // consumed — skip opening the script editor
    }
}
