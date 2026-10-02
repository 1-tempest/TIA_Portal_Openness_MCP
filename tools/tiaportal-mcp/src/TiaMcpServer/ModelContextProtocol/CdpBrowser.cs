using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Headless Microsoft Edge driven over the Chrome DevTools Protocol (raw WebSocket JSON, no
    /// extra packages). Used to look at the WinCC Unified web runtime (simulation) as PNGs.
    /// One browser at a time; every public call is serialized.
    /// </summary>
    internal sealed class CdpBrowser : IDisposable
    {
        private static readonly SemaphoreSlim Sync = new SemaphoreSlim(1, 1);
        private static CdpBrowser? _current;

        private Process? _process;
        private string _profileDir = "";
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _readerCts;
        private int _nextId;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<JsonObject>>();
        private volatile int _loadEvents;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Port { get; private set; }

        public static async Task<T> WithLock<T>(Func<Task<T>> action)
        {
            await Sync.WaitAsync().ConfigureAwait(false);
            try { return await action().ConfigureAwait(false); }
            finally { Sync.Release(); }
        }

        public static CdpBrowser Current
            => _current ?? throw new InvalidOperationException("No HMI browser is running. Call HmiBrowserStart first.");

        public static bool IsRunning => _current != null;

        /// <summary>Edge path: explicit, TIA_MCP_EDGE_PATH, or the standard install locations.</summary>
        public static string FindEdge(string? explicitPath)
        {
            var candidates = new[]
            {
                explicitPath,
                Environment.GetEnvironmentVariable("TIA_MCP_EDGE_PATH"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            };
            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c!;
            }
            throw new FileNotFoundException("msedge.exe not found. Pass edgePath or set TIA_MCP_EDGE_PATH.");
        }

        public static async Task<CdpBrowser> StartAsync(string edgePath, int width, int height, TimeSpan timeout)
        {
            if (_current != null)
            {
                try { await _current.StopAsync().ConfigureAwait(false); } catch { }
            }

            var b = new CdpBrowser { Width = width, Height = height, Port = FreePort() };
            b._profileDir = Path.Combine(Path.GetTempPath(), "TiaMcpEdge_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(b._profileDir);

            var args = string.Join(" ", new[]
            {
                "--headless=new",
                $"--remote-debugging-port={b.Port}",
                "--remote-debugging-address=127.0.0.1",
                $"--user-data-dir=\"{b._profileDir}\"",
                "--ignore-certificate-errors",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                "--disable-sync",
                "--hide-scrollbars",
                "--mute-audio",
                $"--window-size={width},{height}",
                "about:blank"
            });
            b._process = Process.Start(new ProcessStartInfo(edgePath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Edge did not start.");

            try
            {
                var wsUrl = await b.WaitForPageTargetAsync(timeout).ConfigureAwait(false);
                b._ws = new ClientWebSocket();
                await b._ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
                b._readerCts = new CancellationTokenSource();
                _ = Task.Run(() => b.ReadLoop(b._readerCts.Token));

                await b.SendAsync("Page.enable", null).ConfigureAwait(false);
                await b.SendAsync("Runtime.enable", null).ConfigureAwait(false);
                await b.SendAsync("Security.setIgnoreCertificateErrors", new JsonObject { ["ignore"] = true }).ConfigureAwait(false);
                await b.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["deviceScaleFactor"] = 1,
                    ["mobile"] = false
                }).ConfigureAwait(false);
            }
            catch
            {
                await b.StopAsync().ConfigureAwait(false);
                throw;
            }

            _current = b;
            return b;
        }

        /// <summary>Navigates and waits for the load event. Throws with Chromium's errorText when the page is unreachable.</summary>
        public async Task NavigateAsync(string url, TimeSpan timeout)
        {
            var before = _loadEvents;
            var r = await SendAsync("Page.navigate", new JsonObject { ["url"] = url }).ConfigureAwait(false);
            var errorText = r["errorText"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(errorText))
            {
                throw new InvalidOperationException(
                    $"Cannot open {url}: {errorText}. " +
                    (errorText!.Contains("CONNECTION_REFUSED") || errorText.Contains("NAME_NOT_RESOLVED") || errorText.Contains("ADDRESS_UNREACHABLE")
                        ? "The WinCC Unified runtime (simulation) is not running or not listening at this URL. Start the simulation in TIA first."
                        : "Check the URL and that the runtime is running."));
            }
            await WaitForLoadAsync(before, timeout).ConfigureAwait(false);
        }

        public async Task WaitForLoadAsync(int loadEventsBefore, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_loadEvents == loadEventsBefore && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        public int LoadEventCount => _loadEvents;

        public async Task<JsonNode?> EvaluateAsync(string expression)
        {
            var r = await SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
            }).ConfigureAwait(false);
            if (r["exceptionDetails"] is JsonObject ex)
            {
                throw new InvalidOperationException("Page script failed: " + (ex["exception"]?["description"]?.ToString() ?? ex["text"]?.ToString()));
            }
            return r["result"]?["value"]?.DeepClone();
        }

        public async Task ClickAsync(double x, double y, string button, int clickCount)
        {
            await SendAsync("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y }).ConfigureAwait(false);
            for (int i = 1; i <= Math.Max(1, clickCount); i++)
            {
                await SendAsync("Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = 1, ["clickCount"] = i
                }).ConfigureAwait(false);
                await SendAsync("Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = 0, ["clickCount"] = i
                }).ConfigureAwait(false);
            }
        }

        public async Task<byte[]> ScreenshotAsync(int? clipX, int? clipY, int? clipW, int? clipH)
        {
            var p = new JsonObject { ["format"] = "png", ["fromSurface"] = true };
            if (clipW is int w && clipH is int h && w > 0 && h > 0)
            {
                p["clip"] = new JsonObject { ["x"] = clipX ?? 0, ["y"] = clipY ?? 0, ["width"] = w, ["height"] = h, ["scale"] = 1 };
            }
            var r = await SendAsync("Page.captureScreenshot", p).ConfigureAwait(false);
            var data = r["data"]?.GetValue<string>() ?? throw new InvalidOperationException("captureScreenshot returned no data.");
            return Convert.FromBase64String(data);
        }

        public async Task StopAsync()
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    try { await SendAsync("Browser.close", null, TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
                }
            }
            finally
            {
                Dispose();
                if (ReferenceEquals(_current, this)) _current = null;
            }
        }

        public void Dispose()
        {
            try { _readerCts?.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    if (!_process.WaitForExit(2000)) _process.Kill();
                }
            }
            catch { }
            _process = null;
            for (int i = 0; i < 5 && Directory.Exists(_profileDir); i++)
            {
                try { Directory.Delete(_profileDir, true); }
                catch { Thread.Sleep(300); }
            }
            foreach (var kv in _pending) kv.Value.TrySetException(new ObjectDisposedException("CdpBrowser"));
            _pending.Clear();
        }

        public Task<JsonObject> SendAsync(string method, JsonObject? parameters)
            => SendAsync(method, parameters, TimeSpan.FromSeconds(30));

        public async Task<JsonObject> SendAsync(string method, JsonObject? parameters, TimeSpan timeout)
        {
            var ws = _ws ?? throw new InvalidOperationException("Browser connection is closed.");
            if (_process != null && _process.HasExited) throw new InvalidOperationException("Edge has exited. Call HmiBrowserStart again.");

            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var msg = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters != null) msg["params"] = parameters;
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
            _pending.TryRemove(id, out _);
            if (done != tcs.Task) throw new TimeoutException($"{method} timed out after {timeout.TotalSeconds:0} s.");
            return await tcs.Task.ConfigureAwait(false);
        }

        private async Task ReadLoop(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var sb = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;

                    var text = Encoding.UTF8.GetString(sb.ToArray());
                    sb.SetLength(0);
                    JsonObject? obj;
                    try { obj = JsonNode.Parse(text) as JsonObject; }
                    catch { continue; }
                    if (obj == null) continue;

                    if (obj["id"] is JsonValue idv && idv.TryGetValue<int>(out var id) && _pending.TryGetValue(id, out var tcs))
                    {
                        if (obj["error"] is JsonObject err)
                            tcs.TrySetException(new InvalidOperationException($"CDP error: {err["message"]} {err["data"]}"));
                        else
                            tcs.TrySetResult(obj["result"] as JsonObject ?? new JsonObject());
                    }
                    else if (obj["method"]?.ToString() == "Page.loadEventFired")
                    {
                        Interlocked.Increment(ref _loadEvents);
                    }
                }
            }
            catch
            {
                // connection closed
            }
            foreach (var kv in _pending) kv.Value.TrySetException(new InvalidOperationException("Browser connection closed."));
        }

        private async Task<string> WaitForPageTargetAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                if (_process != null && _process.HasExited)
                    throw new InvalidOperationException($"Edge exited at startup (code {_process.ExitCode}).");
                try
                {
                    using (var wc = new WebClient())
                    {
                        var json = await wc.DownloadStringTaskAsync($"http://127.0.0.1:{Port}/json/list").ConfigureAwait(false);
                        var page = (JsonNode.Parse(json) as JsonArray)?
                            .OfType<JsonObject>()
                            .FirstOrDefault(t => t["type"]?.ToString() == "page" && t["webSocketDebuggerUrl"] != null);
                        if (page != null) return page["webSocketDebuggerUrl"]!.ToString();
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            throw new TimeoutException($"Edge DevTools endpoint on port {Port} did not come up: {last?.Message}");
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
