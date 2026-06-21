using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityExplorerTLD.MCP
{
    /// <summary>
    /// Minimal, spec-aligned MCP (Model Context Protocol) server.
    /// JSON-RPC 2.0 over HTTP POST (the single-JSON-response variant of the
    /// Streamable HTTP transport - no SSE). Bridges Claude Code (or any MCP
    /// client) to UnityExplorer's C# console.
    /// </summary>
    public static class McpServer
    {
        public const string SERVER_NAME = "UnityExplorerTLD.MCP";
        public const string SERVER_VERSION = "1.0.0";
        public const int DEFAULT_PORT = 3000;

        // Latest MCP protocol revision we implement; older revisions are accepted for compatibility.
        public const string LATEST_PROTOCOL = "2025-06-18";
        private static readonly string[] SUPPORTED_PROTOCOLS = { "2025-06-18", "2025-03-26", "2024-11-05" };

        private static HttpListener listener;
        private static Thread listenerThread;
        private static bool isRunning;
        private static MelonLogger.Instance log;

        // Queue draining onto Unity's main thread (code must run there).
        private static readonly Queue<CodeExecutionRequest> executionQueue = new Queue<CodeExecutionRequest>();
        private static readonly object queueLock = new object();

        private class CodeExecutionRequest
        {
            public string Code;
            public Action<string> Callback;
        }

        // ---- lifecycle -------------------------------------------------------

        public static void Start(int port, MelonLogger.Instance logger)
        {
            log = logger;

            if (isRunning)
            {
                log?.Warning("MCP server is already running.");
                return;
            }

            listener = new HttpListener();
            // Bind to all interfaces so a WSL client can reach it.
            // (Windows may need a URL ACL: netsh http add urlacl url=http://+:3000/ user=Everyone)
            listener.Prefixes.Add($"http://+:{port}/");
            listener.Start();
            isRunning = true;

            listenerThread = new Thread(ListenForRequests) { IsBackground = true, Name = "MCP-Listener" };
            listenerThread.Start();

            log?.Msg($"MCP server started (protocol {LATEST_PROTOCOL}). Reachable at:");
            foreach (string addr in GetLocalIPAddresses())
                log?.Msg($"  http://{addr}:{port}/");
            log?.Msg("Claude Code can now connect to UnityExplorer.");
        }

        public static void Stop()
        {
            if (!isRunning)
                return;

            isRunning = false;
            try { listener?.Stop(); listener?.Close(); } catch { /* shutting down */ }
            log?.Msg("MCP server stopped.");
        }

        /// <summary>Called every frame from Unity's main thread; drains queued code.</summary>
        public static void Update()
        {
            if (!isRunning)
                return;

            while (true)
            {
                CodeExecutionRequest request = null;
                lock (queueLock)
                {
                    if (executionQueue.Count > 0)
                        request = executionQueue.Dequeue();
                }

                if (request == null)
                    break;

                string result = ExecuteCSharpCode(request.Code);
                request.Callback?.Invoke(result);
            }
        }

        // ---- HTTP / transport ------------------------------------------------

        private static void ListenForRequests()
        {
            while (isRunning)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    Task.Run(() => HandleRequest(context));
                }
                catch
                {
                    if (!isRunning)
                        break;
                    // Otherwise keep listening. Never log from this background thread.
                }
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest req = context.Request;
                string origin = req.Headers["Origin"];

                // CORS pre-flight.
                if (req.HttpMethod == "OPTIONS") { SendEmpty(context, 204, origin); return; }

                // MUST validate Origin to prevent DNS-rebinding attacks (MCP transport spec).
                if (!IsOriginAllowed(origin)) { SendEmpty(context, 403, origin); return; }

                // Single MCP endpoint must accept POST and GET. GET is the server->client
                // SSE stream, which this minimal server does not offer -> 405.
                if (req.HttpMethod == "GET") { SendEmpty(context, 405, origin); return; }
                if (req.HttpMethod != "POST") { SendEmpty(context, 405, origin); return; }

                // If the client pins a protocol version, it must be one we support.
                string protoHeader = req.Headers["MCP-Protocol-Version"];
                if (!string.IsNullOrEmpty(protoHeader) && Array.IndexOf(SUPPORTED_PROTOCOLS, protoHeader) < 0)
                { SendEmpty(context, 400, origin); return; }

                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = reader.ReadToEnd();

                JObject request;
                try { request = JObject.Parse(body); }
                catch (Exception ex) { SendJson(context, CreateErrorResponse(null, -32700, $"Parse error: {ex.Message}"), origin); return; }

                string method = request["method"]?.ToString();
                JToken id = request["id"];
                bool isRequest = id != null && id.Type != JTokenType.Null;

                // JSON-RPC notifications / responses carry no id. Per the transport spec
                // these get 202 Accepted with no body (e.g. notifications/initialized).
                if (!isRequest)
                {
                    SendEmpty(context, 202, origin);
                    return;
                }

                JObject response;
                switch (method)
                {
                    case "initialize": response = HandleInitialize(id, request["params"] as JObject); break;
                    case "ping": response = WrapResult(id, new JObject()); break;
                    case "tools/list": response = HandleToolsList(id); break;
                    case "tools/call": response = HandleToolsCall(id, request["params"] as JObject); break;
                    default: response = CreateErrorResponse(id, -32601, $"Method not found: {method}"); break;
                }

                SendJson(context, response, origin);
            }
            catch
            {
                try { SendEmpty(context, 500, null); } catch { /* give up */ }
            }
        }

        private static bool IsOriginAllowed(string origin)
        {
            // Non-browser clients (Claude Code, curl) send no Origin header.
            if (string.IsNullOrEmpty(origin))
                return true;

            try
            {
                string host = new Uri(origin).Host;
                return host == "localhost" || host == "::1" || host.StartsWith("127.");
            }
            catch { return false; }
        }

        // ---- MCP methods -----------------------------------------------------

        private static JObject HandleInitialize(JToken id, JObject parameters)
        {
            string requested = parameters?["protocolVersion"]?.ToString();
            string agreed = (!string.IsNullOrEmpty(requested) && Array.IndexOf(SUPPORTED_PROTOCOLS, requested) >= 0)
                ? requested
                : LATEST_PROTOCOL;

            return WrapResult(id, new JObject
            {
                ["protocolVersion"] = agreed,
                ["serverInfo"] = new JObject { ["name"] = SERVER_NAME, ["version"] = SERVER_VERSION },
                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } }
            });
        }

        private static JObject HandleToolsList(JToken id)
        {
            return WrapResult(id, new JObject
            {
                ["tools"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "unity_execute_csharp",
                        ["description"] = "Execute C# code in the running Unity game via UnityExplorer's console, " +
                                          "and return the output of THIS execution directly. " +
                                          "Use it to inspect game objects, call methods, and manipulate the game. " +
                                          "REPL helpers: CurrentTarget, AllTargets, Log(obj), Inspect(obj), Start(enumerator), Copy(obj), Paste(). " +
                                          "The response contains the REPL return value, anything the code logged, and any compile/runtime errors. " +
                                          "(Output from coroutines/async runs later - use unity_read_log for that.)",
                        ["inputSchema"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["code"] = new JObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "The C# code to execute. REPL expressions, using directives, or class definitions."
                                }
                            },
                            ["required"] = new JArray { "code" }
                        }
                    },
                    new JObject
                    {
                        ["name"] = "unity_read_log",
                        ["description"] = "Read recent entries from UnityExplorer's log panel. " +
                                          "Useful for output produced after a command returns (coroutines, async, game events).",
                        ["inputSchema"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["count"] = new JObject
                                {
                                    ["type"] = "number",
                                    ["description"] = "Number of recent log entries to retrieve (default: 20, max: 100)."
                                }
                            },
                            ["required"] = new JArray()
                        }
                    }
                }
            });
        }

        private static JObject HandleToolsCall(JToken id, JObject parameters)
        {
            string toolName = parameters?["name"]?.ToString();
            JObject arguments = parameters?["arguments"] as JObject;

            if (toolName == "unity_execute_csharp")
            {
                string code = arguments?["code"]?.ToString();
                if (string.IsNullOrEmpty(code))
                    return CreateErrorResponse(id, -32602, "Missing required parameter: code");

                string result = null;
                using (var done = new ManualResetEvent(false))
                {
                    lock (queueLock)
                    {
                        executionQueue.Enqueue(new CodeExecutionRequest
                        {
                            Code = code,
                            Callback = r => { result = r; done.Set(); }
                        });
                    }

                    if (!done.WaitOne(30000))
                        return CreateErrorResponse(id, -32603, "Code execution timed out (30s).");
                }

                return ToolTextResult(id, result);
            }

            if (toolName == "unity_read_log")
            {
                int count = 20;
                JToken countTok = arguments?["count"];
                if (countTok != null && countTok.Type != JTokenType.Null)
                    count = Math.Min(100, Math.Max(1, (int)countTok));

                return ToolTextResult(id, ReadRecentLogs(count));
            }

            return CreateErrorResponse(id, -32602, $"Unknown tool: {toolName}");
        }

        // ---- UnityExplorer bridge (main thread only) -------------------------
        //
        // Everything here binds to UnityExplorer by reflection so the addon keeps
        // working across UnityExplorer builds. As of the public 6.0.0 release the
        // relevant shapes are:
        //   ConsoleController : singleton (static _instance); instance public
        //                       Evaluate(string, bool); instance non-public
        //                       _evaluator (null until ready) and sreNotSupported.
        //   LogPanel          : static non-public List<LogInfo> Logs;
        //                       LogInfo has public string message / LogType type.

        private static bool consoleResolved;
        private static Type ccType;
        private static PropertyInfo ccInstanceProp;
        private static FieldInfo ccInstanceField;
        private static MethodInfo ccEvaluate;
        private static FieldInfo ccEvaluatorField;
        private static PropertyInfo ccSreProp;
        private static PropertyInfo ccPanelProp;   // touching this builds the CSConsole panel
        private static MethodInfo ccInit;          // static ConsoleController.Init()
        private static bool initAttempted;

        private static FieldInfo logsField;
        private static FieldInfo logMessageField;
        private static FieldInfo logTypeField;

        private static string lastConsoleDiag = "(no diagnostic yet)";

        private static Type FindType(string fullName)
        {
            try
            {
                Type t = Type.GetType(fullName + ", UnityExplorerTLD");
                if (t != null) return t;
            }
            catch { }
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = asm.GetType(fullName); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static void ResolveConsole()
        {
            if (consoleResolved)
                return;
            consoleResolved = true;
            try
            {
                ccType = FindType("UnityExplorer.CSConsole.ConsoleController");
                if (ccType == null)
                    return;

                ccInstanceProp = ccType.GetProperty("_instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                ccInstanceField = ccType.GetField("<_instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
                ccEvaluate = ccType.GetMethod("Evaluate", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(string), typeof(bool) }, null);
                ccEvaluatorField = ccType.GetField("_evaluator", BindingFlags.Instance | BindingFlags.NonPublic);
                ccSreProp = ccType.GetProperty("sreNotSupported", BindingFlags.Instance | BindingFlags.NonPublic);
                ccPanelProp = ccType.GetProperty("_panel", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                ccInit = ccType.GetMethod("Init", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            }
            catch { /* leave whatever resolved */ }
        }

        private static object ReadConsoleInstance()
        {
            object inst = null;
            try { inst = ccInstanceProp?.GetValue(null); } catch { }
            if (inst == null)
            {
                try { inst = ccInstanceField?.GetValue(null); } catch { }
            }
            return inst;
        }

        /// <summary>
        /// Returns the ConsoleController singleton, forcing UnityExplorer to build
        /// its C# console if it hasn't yet (it is created lazily when the panel is
        /// first shown - which the user can't do while the game has the cursor
        /// locked). Must be called on the main thread.
        /// </summary>
        private static object GetConsoleInstance()
        {
            var d = new StringBuilder();
            ResolveConsole();
            d.AppendLine($"ccType={(ccType != null ? ccType.Assembly.GetName().Name : "NULL")}");
            if (ccType == null) { lastConsoleDiag = d.ToString(); return null; }

            d.AppendLine($"members: instProp={(ccInstanceProp != null)} instField={(ccInstanceField != null)} evaluate={(ccEvaluate != null)} panelProp={(ccPanelProp != null)} init={(ccInit != null)}");

            object inst = ReadConsoleInstance();
            d.AppendLine($"instance(initial)={(inst == null ? "null" : inst.GetType().Name)}");
            if (inst != null) { lastConsoleDiag = d.ToString(); return inst; }

            // Strategy 1: touch the panel property -> UIManager builds the CSConsole panel.
            try { object p = ccPanelProp?.GetValue(null); d.AppendLine($"panelTouch -> {(p == null ? "null" : p.GetType().Name)}"); }
            catch (Exception ex) { d.AppendLine($"panelTouch EX: {Inner(ex)}"); }
            inst = ReadConsoleInstance();
            d.AppendLine($"instance(after panel)={(inst == null ? "null" : inst.GetType().Name)}");

            // Strategy 2: call ConsoleController.Init() directly (once).
            if (inst == null && !initAttempted)
            {
                initAttempted = true;
                try { ccInit?.Invoke(null, null); d.AppendLine("Init() invoked"); }
                catch (Exception ex) { d.AppendLine($"Init() EX: {Inner(ex)}"); }
                inst = ReadConsoleInstance();
                d.AppendLine($"instance(after Init)={(inst == null ? "null" : inst.GetType().Name)}");
            }

            lastConsoleDiag = d.ToString();
            return inst;
        }

        private static string Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static IList GetLogList()
        {
            try
            {
                if (logsField == null)
                {
                    Type logPanel = FindType("UnityExplorer.UI.Panels.LogPanel");
                    logsField = logPanel?.GetField("Logs", BindingFlags.Static | BindingFlags.NonPublic);
                }
                return logsField?.GetValue(null) as IList;
            }
            catch { return null; }
        }

        private static string FormatLogEntry(object entry)
        {
            if (entry == null) return "";
            Type t = entry.GetType();
            if (logMessageField == null || logMessageField.DeclaringType != t)
            {
                logMessageField = t.GetField("message");
                logTypeField = t.GetField("type");
            }
            string message = logMessageField?.GetValue(entry)?.ToString() ?? "";
            string type = logTypeField?.GetValue(entry)?.ToString() ?? "Log";
            return $"[{type}] {message}";
        }

        /// <summary>
        /// Runs the code synchronously on the main thread and returns the log
        /// entries it produced (REPL result, Log() output, compile/runtime errors).
        /// </summary>
        private static string ExecuteCSharpCode(string code)
        {
            try
            {
                object console = GetConsoleInstance();
                if (console == null || ccEvaluate == null)
                    return "Error: UnityExplorer's C# console is not available yet.\n--- diagnostic ---\n" + lastConsoleDiag;

                try
                {
                    if (ccEvaluatorField != null && ccEvaluatorField.GetValue(console) == null)
                        return "Error: UnityExplorer's C# console evaluator is not ready. Open the C# Console tab once, then retry.";
                    if (ccSreProp != null && ccSreProp.GetValue(console) is bool sre && sre)
                        return "Error: C# console is disabled on this build (SRE not supported).";
                }
                catch { /* readiness probe is best-effort */ }

                IList logs = GetLogList();
                int before = logs?.Count ?? -1;

                ccEvaluate.Invoke(console, new object[] { code, false });

                if (logs == null || before < 0)
                    return "Code executed. (Could not capture console output on this build.)";

                int after = logs.Count;
                if (after <= before)
                    return "Code executed. (No output.)";

                var sb = new StringBuilder();
                for (int i = before; i < after; i++)
                    sb.AppendLine(FormatLogEntry(logs[i]));

                string output = sb.ToString().TrimEnd();
                const int max = 16000;
                if (output.Length > max)
                    output = output.Substring(0, max) + "\n... (output truncated)";
                return output;
            }
            catch (Exception ex)
            {
                return $"Error executing code: {ex.Message}\n{ex.StackTrace}";
            }
        }

        private static string ReadRecentLogs(int count)
        {
            try
            {
                IList logs = GetLogList();
                if (logs == null)
                    return "Error: could not access UnityExplorer's log data.";
                if (logs.Count == 0)
                    return "No log entries found.";

                int start = Math.Max(0, logs.Count - count);
                var sb = new StringBuilder();
                sb.AppendLine($"=== Recent log entries ({logs.Count - start} of {logs.Count}) ===");
                for (int i = start; i < logs.Count; i++)
                    sb.AppendLine(FormatLogEntry(logs[i]));
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error reading logs: {ex.Message}";
            }
        }

        // ---- JSON-RPC helpers ------------------------------------------------

        private static JObject WrapResult(JToken id, JObject result)
        {
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JObject ToolTextResult(JToken id, string text)
        {
            return WrapResult(id, new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text ?? "" } }
            });
        }

        private static JObject CreateErrorResponse(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };
        }

        // ---- response writers ------------------------------------------------

        private static void SendJson(HttpListenerContext context, JObject obj, string origin)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
            HttpListenerResponse resp = context.Response;
            resp.StatusCode = 200;
            resp.ContentType = "application/json";
            resp.ContentLength64 = buffer.Length;
            AddCors(resp, origin);
            resp.OutputStream.Write(buffer, 0, buffer.Length);
            resp.OutputStream.Close();
        }

        private static void SendEmpty(HttpListenerContext context, int statusCode, string origin)
        {
            HttpListenerResponse resp = context.Response;
            resp.StatusCode = statusCode;
            resp.ContentLength64 = 0;
            AddCors(resp, origin);
            resp.OutputStream.Close();
        }

        private static void AddCors(HttpListenerResponse resp, string origin)
        {
            resp.AddHeader("Access-Control-Allow-Origin", string.IsNullOrEmpty(origin) ? "*" : origin);
            resp.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            resp.AddHeader("Access-Control-Allow-Headers", "Content-Type, MCP-Protocol-Version, Mcp-Session-Id");
        }

        private static List<string> GetLocalIPAddresses()
        {
            var addresses = new List<string>();
            try
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !ip.ToString().StartsWith("127."))
                        addresses.Add(ip.ToString());
                }
            }
            catch { /* best effort */ }
            return addresses;
        }
    }
}
