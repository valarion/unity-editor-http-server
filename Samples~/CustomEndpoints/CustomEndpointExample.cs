// ---------------------------------------------------------------------------
// Custom endpoint example for Unity Editor HTTP Server
//
// HOW TO USE
// ----------
// 1. Copy this file (or create your own) anywhere inside your project's
//    Assets/Editor/ folder (it must be Editor-only).
// 2. Modify the endpoint path, method, and handler logic as needed.
// 3. Unity will recompile and the endpoint becomes available immediately
//    at http://localhost:8765/your/path
// 4. Test with curl:  curl http://localhost:8765/your/path
// 5. Once happy, open a PR to the main repository to add it as a built-in
//    endpoint.
//
// HOW IT WORKS
// ------------
// [InitializeOnLoad] causes Unity to run this static constructor on every
// recompile (and on Editor startup). TestHttpServer.RegisterEndpoint() adds
// the route to an in-memory dictionary that the server checks after its own
// built-in routes.
//
// MAIN-THREAD UNITY APIs
// ----------------------
// HTTP requests arrive on a background thread. Any Unity API (AssetDatabase,
// EditorApplication, etc.) must run on the main thread. Use:
//
//   var result = TestHttpServer.RunOnMainThread(() => SomeUnityApi.Call());
//
// ---------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class CustomEndpointExample
{
    static CustomEndpointExample()
    {
        // -----------------------------------------------------------------------
        // Example 1: GET /custom/hello
        // Simple endpoint with no Unity API calls — no main-thread dispatch needed.
        // -----------------------------------------------------------------------
        TestHttpServer.RegisterEndpoint("GET", "/custom/hello", ctx =>
        {
            TestHttpServer.Respond(ctx, 200, "{\"message\":\"Hello from a custom endpoint!\"}");
        });

        // -----------------------------------------------------------------------
        // Example 2: GET /custom/scene
        // Returns the currently open scene name. Uses a Unity API, so it must
        // dispatch to the main thread via RunOnMainThread.
        // -----------------------------------------------------------------------
        TestHttpServer.RegisterEndpoint("GET", "/custom/scene", ctx =>
        {
            var sceneName = TestHttpServer.RunOnMainThread(() =>
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);

            TestHttpServer.Respond(ctx, 200, $"{{\"scene\":{TestHttpServer.JsonStr(sceneName)}}}");
        });

        // -----------------------------------------------------------------------
        // Example 3: POST /custom/log
        // Reads a JSON body and writes to the Unity console.
        // -----------------------------------------------------------------------
        TestHttpServer.RegisterEndpoint("POST", "/custom/log", ctx =>
        {
            string message;
            using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8))
            {
                var body = reader.ReadToEnd().Trim();
                // Very simple extraction — for production code use a proper JSON parser
                var match = System.Text.RegularExpressions.Regex.Match(body, @"""message""\s*:\s*""([^""]+)""");
                message = match.Success ? match.Groups[1].Value : body;
            }

            TestHttpServer.RunOnMainThread(() => Debug.Log($"[HTTP] {message}"));
            TestHttpServer.Respond(ctx, 200, $"{{\"logged\":{TestHttpServer.JsonStr(message)}}}");
        });
    }
}
