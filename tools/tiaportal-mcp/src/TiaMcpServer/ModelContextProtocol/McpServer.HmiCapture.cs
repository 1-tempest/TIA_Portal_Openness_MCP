using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    // Looking at the WinCC Unified runtime (simulation) as PNGs. None of these tools touch the TIA
    // project. Images are written to the server work folder and, unless includeImage=false, also
    // returned as an MCP image content block after the usual {"message","meta"} text block.
    public static partial class McpServer
    {
        // V21 public Openness has no API to start/stop the Unified runtime simulation (no
        // Simulation/StartRuntime member in Siemens.Engineering.*); it is started by hand in TIA.
        private const string DefaultWebRtUrl = "https://localhost/WebRH";

        [McpServerTool(Name = "CaptureDesktop"), Description(
            "[L2][HMI-View] PNG of the server's whole desktop (fallback for viewing the HMI simulation window). Saved in the work folder; " +
            "returned as image content too. Optional crop (pixels). Needs an interactive desktop: a server running as a service gets black images (meta.looksBlank).")]
        public static CallToolResult CaptureDesktop(
            [Description("Work-folder path for the PNG; empty = captures/desktop_<time>.png")] string path = "",
            [Description("Crop X")] int cropX = 0,
            [Description("Crop Y")] int cropY = 0,
            [Description("Crop width (0 = no crop)")] int cropWidth = 0,
            [Description("Crop height (0 = no crop)")] int cropHeight = 0,
            [Description("Also return the PNG as image content")] bool includeImage = true)
        {
            try
            {
                var r = ScreenCapture.CaptureDesktop(Crop(cropX, cropY, cropWidth, cropHeight));
                return ImageResult("Desktop captured.", r.Png, Def(path, "desktop"), includeImage, new JsonObject
                {
                    ["bounds"] = $"{r.Bounds.X},{r.Bounds.Y},{r.Bounds.Width}x{r.Bounds.Height}",
                    ["looksBlank"] = r.LooksBlank
                });
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException("CaptureDesktop failed: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "CaptureWindow"), Description(
            "[L2][HMI-View] PNG of the first visible top-level window whose title matches titleRegex (PrintWindow, BitBlt fallback), e.g. the " +
            "Unified simulation/runtime window. Saved in the work folder and returned as image content. No match: the error lists visible window titles.")]
        public static CallToolResult CaptureWindow(
            [Description("Regex matched against window titles (case-insensitive)")] string titleRegex,
            [Description("Work-folder path for the PNG; empty = captures/window_<time>.png")] string path = "",
            [Description("Crop X (relative to the window)")] int cropX = 0,
            [Description("Crop Y")] int cropY = 0,
            [Description("Crop width (0 = no crop)")] int cropWidth = 0,
            [Description("Crop height (0 = no crop)")] int cropHeight = 0,
            [Description("Also return the PNG as image content")] bool includeImage = true)
        {
            try
            {
                var r = ScreenCapture.CaptureWindow(titleRegex, Crop(cropX, cropY, cropWidth, cropHeight));
                return ImageResult($"Window '{r.WindowTitle}' captured.", r.Png, Def(path, "window"), includeImage, new JsonObject
                {
                    ["windowTitle"] = r.WindowTitle,
                    ["bounds"] = $"{r.Bounds.X},{r.Bounds.Y},{r.Bounds.Width}x{r.Bounds.Height}",
                    ["looksBlank"] = r.LooksBlank
                });
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException("CaptureWindow failed: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "HmiBrowserStart"), Description(
            "[L2][HMI-View] Start headless Microsoft Edge (DevTools protocol, self-signed certs accepted, temp profile) at width x height and open the " +
            "WinCC Unified web runtime (default " + DefaultWebRtUrl + "). If a login page appears and user/password are given, logs in. " +
            "Start the HMI simulation in TIA first (Openness V21 cannot start it). Then HmiBrowserClick / HmiBrowserScreenshot; HmiBrowserStop at the end.")]
        public static async Task<ResponseMessage> HmiBrowserStart(
            [Description("Runtime URL")] string url = DefaultWebRtUrl,
            [Description("Viewport width (MTP1200: 1280)")] int width = 1280,
            [Description("Viewport height (MTP1200: 800)")] int height = 800,
            [Description("Runtime user (only if a login page appears)")] string user = "",
            [Description("Runtime password")] string password = "",
            [Description("msedge.exe path; empty = TIA_MCP_EDGE_PATH or the standard install")] string edgePath = "",
            [Description("Seconds to wait for Edge and the page")] int timeoutSeconds = 30)
        {
            try
            {
                return await CdpBrowser.WithLock(async () =>
                {
                    var timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
                    var b = await CdpBrowser.StartAsync(CdpBrowser.FindEdge(edgePath), width, height, timeout).ConfigureAwait(false);
                    var meta = new JsonObject { ["devtoolsPort"] = b.Port, ["width"] = width, ["height"] = height };
                    try
                    {
                        await b.NavigateAsync(url, timeout).ConfigureAwait(false);
                    }
                    catch
                    {
                        await b.StopAsync().ConfigureAwait(false);
                        throw;
                    }
                    await Task.Delay(1500).ConfigureAwait(false); // SPA bootstrap / redirect to the login page

                    var login = await TryLoginAsync(b, user, password, timeout).ConfigureAwait(false);
                    meta["login"] = login;
                    meta["url"] = (await b.EvaluateAsync("location.href").ConfigureAwait(false))?.ToString();
                    meta["title"] = (await b.EvaluateAsync("document.title").ConfigureAwait(false))?.ToString();
                    var msg = login == "required"
                        ? "Browser started; the runtime shows a login page. Call HmiBrowserStart again with user/password."
                        : login == "failed"
                            ? "Browser started, but the login page is still shown after submitting (wrong user/password?)."
                            : "Browser started.";
                    return new ResponseMessage { Message = msg, Meta = meta };
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException("HmiBrowserStart failed: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "HmiBrowserNavigate"), Description("[L2][HMI-View] Open another URL in the running HMI browser and wait for it to load.")]
        public static async Task<ResponseMessage> HmiBrowserNavigate(
            [Description("URL")] string url,
            [Description("Seconds to wait for the load event")] int timeoutSeconds = 30)
        {
            try
            {
                return await CdpBrowser.WithLock(async () =>
                {
                    var b = CdpBrowser.Current;
                    await b.NavigateAsync(url, TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))).ConfigureAwait(false);
                    return new ResponseMessage { Message = "Loaded " + url, Meta = new JsonObject { ["url"] = (await b.EvaluateAsync("location.href").ConfigureAwait(false))?.ToString() } };
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException("HmiBrowserNavigate failed: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "HmiBrowserClick"), Description(
            "[L2][HMI-View] Left-click at viewport pixel (x, y) in the HMI browser (e.g. a navigation button), then wait waitMs for the screen change.")]
        public static async Task<ResponseMessage> HmiBrowserClick(
            [Description("X in viewport pixels")] double x,
            [Description("Y in viewport pixels")] double y,
            [Description("Milliseconds to wait after the click")] int waitMs = 800,
            [Description("2 = double click")] int clickCount = 1)
        {
            try
            {
                return await CdpBrowser.WithLock(async () =>
                {
                    await CdpBrowser.Current.ClickAsync(x, y, "left", clickCount).ConfigureAwait(false);
                    if (waitMs > 0) await Task.Delay(Math.Min(waitMs, 60000)).ConfigureAwait(false);
                    return new ResponseMessage { Message = $"Clicked at {x},{y}.", Meta = new JsonObject { ["x"] = x, ["y"] = y } };
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException("HmiBrowserClick failed: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "HmiBrowserWait"), Description("[L2][HMI-View] Wait ms milliseconds (max 60000), e.g. for runtime values or animations to settle.")]
        public static async Task<ResponseMessage> HmiBrowserWait(
            [Description("Milliseconds")] int ms = 1000)
        {
            await Task.Delay(Math.Max(0, Math.Min(ms, 60000))).ConfigureAwait(false);
            return new ResponseMessage { Message = $"Waited {ms} ms.", Meta = new JsonObject { ["browserRunning"] = CdpBrowser.IsRunning } };
        }

        [McpServerTool(Name = "HmiBrowserScreenshot"), Description(
            "[L2][HMI-View] PNG of the HMI browser viewport (Page.captureScreenshot), optional clip rectangle. Saved in the work folder " +
            "(ReadWorkFile base64 works) and returned as image content.")]
        public static async Task<CallToolResult> HmiBrowserScreenshot(
            [Description("Work-folder path for the PNG; empty = captures/hmi_<time>.png")] string path = "",
            [Description("Clip X")] int clipX = 0,
            [Description("Clip Y")] int clipY = 0,
            [Description("Clip width (0 = whole viewport)")] int clipWidth = 0,
            [Description("Clip height (0 = whole viewport)")] int clipHeight = 0,
            [Description("Also return the PNG as image content")] bool includeImage = true)
        {
            try
            {
                return await CdpBrowser.WithLock(async () =>
                {
                    var b = CdpBrowser.Current;
                    var png = await b.ScreenshotAsync(clipX, clipY, clipWidth, clipHeight).ConfigureAwait(false);
                    return ImageResult("Screenshot taken.", png, Def(path, "hmi"), includeImage, new JsonObject
                    {
                        ["url"] = (await b.EvaluateAsync("location.href").ConfigureAwait(false))?.ToString(),
                        ["viewport"] = $"{b.Width}x{b.Height}"
                    });
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException("HmiBrowserScreenshot failed: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "HmiBrowserStop"), Description("[L2][HMI-View] Close the HMI browser and delete its temporary profile.")]
        public static async Task<ResponseMessage> HmiBrowserStop()
        {
            return await CdpBrowser.WithLock(async () =>
            {
                if (!CdpBrowser.IsRunning) return new ResponseMessage { Message = "No HMI browser was running." };
                await CdpBrowser.Current.StopAsync().ConfigureAwait(false);
                return new ResponseMessage { Message = "HMI browser stopped." };
            }).ConfigureAwait(false);
        }

        /// <summary>"none" (no login page) | "required" (no credentials given) | "ok" | "failed".</summary>
        private static async Task<string> TryLoginAsync(CdpBrowser b, string user, string password, TimeSpan timeout)
        {
            const string hasPassword = "!!document.querySelector('input[type=password]')";
            if (!((await b.EvaluateAsync(hasPassword).ConfigureAwait(false))?.GetValue<bool>() ?? false)) return "none";
            if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(password)) return "required";

            var script = @"(function(u, p) {
  const pw = document.querySelector('input[type=password]');
  if (!pw) return 'no-login';
  const visible = el => el && el.type !== 'hidden' && el.offsetParent !== null;
  const user = [...document.querySelectorAll('input')].find(i => visible(i) && i.type !== 'password' && i.type !== 'checkbox' && i.type !== 'submit');
  const set = (el, v) => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
  if (user) set(user, u);
  set(pw, p);
  const btn = document.querySelector('button[type=submit],input[type=submit]')
    || [...document.querySelectorAll('button')].find(x => /log ?in|sign ?in|anmelden|ok/i.test(x.textContent || ''));
  if (btn) { btn.click(); return 'button'; }
  if (pw.form) { pw.form.submit(); return 'form'; }
  pw.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true }));
  return 'enter';
})(" + JsonSerializer.Serialize(user ?? "") + ", " + JsonSerializer.Serialize(password ?? "") + ")";

            var before = b.LoadEventCount;
            await b.EvaluateAsync(script).ConfigureAwait(false);
            await b.WaitForLoadAsync(before, timeout).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
            var still = (await b.EvaluateAsync(hasPassword).ConfigureAwait(false))?.GetValue<bool>() ?? false;
            return still ? "failed" : "ok";
        }

        private static Rectangle? Crop(int x, int y, int w, int h)
            => w > 0 && h > 0 ? new Rectangle(x, y, w, h) : (Rectangle?)null;

        private static string Def(string path, string kind)
            => string.IsNullOrWhiteSpace(path) ? $"captures/{kind}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png" : path;

        /// <summary>{"message","meta"} text block (same envelope as every other tool) + optional PNG image block.</summary>
        private static CallToolResult ImageResult(string message, byte[] png, string path, bool includeImage, JsonObject meta)
        {
            var b64 = Convert.ToBase64String(png);
            WorkFolder.Write(path, b64, base64: true, overwrite: true, append: false);
            meta["path"] = path;
            meta["fullPath"] = WorkFolder.Resolve(path);
            meta["bytes"] = png.Length;
            if (TryPngSize(png, out var w, out var h))
            {
                meta["width"] = w;
                meta["height"] = h;
            }
            meta["imageIncluded"] = includeImage;

            var envelope = new JsonObject { ["message"] = message, ["meta"] = meta };
            var blocks = new List<ContentBlock> { new TextContentBlock { Text = envelope.ToJsonString() } };
            if (includeImage) blocks.Add(new ImageContentBlock { Data = b64, MimeType = "image/png" });
            return new CallToolResult { Content = blocks };
        }

        private static bool TryPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P') return false;
            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return true;
        }
    }
}
