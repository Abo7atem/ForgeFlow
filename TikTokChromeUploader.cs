using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ForgeFlow
{
    internal sealed class TikTokChromeUploader
    {
        private const string UploadUrl = "https://www.tiktok.com/tiktokstudio/upload?from=webapp";
        private readonly string _profileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForgeFlow", "Chrome", "TikTokProfile");

        public event Action<string>? StatusChanged;

        public async Task<TikTokUploadResult> OpenStudioAsync(CancellationToken cancellationToken)
        {
            string? chromePath = FindChromeExecutable();
            if (chromePath == null)
                return TikTokUploadResult.Failed("لم يتم العثور على Google Chrome. ثبّت Chrome ثم أعد المحاولة.");

            Directory.CreateDirectory(_profileDirectory);
            StatusChanged?.Invoke("تحضير Chrome العادي لتسجيل دخول Google/TikTok...");
            await CloseDebugChromeIfRunningAsync(cancellationToken).ConfigureAwait(false);
            StartLoginChrome(chromePath);
            return TikTokUploadResult.Succeeded(
                "تم فتح Chrome العادي بدون تحكم برمجي. سجّل الدخول إلى TikTok، ثم أغلق نافذة Chrome هذه بالكامل وارجع واضغط «رفع عبر Chrome».");
        }

        public async Task<TikTokUploadResult> UploadAsync(string videoPath, string caption, CancellationToken cancellationToken)
        {
            if (!File.Exists(videoPath))
                return TikTokUploadResult.Failed("ملف الفيديو غير موجود.");

            (int? port, string? chromeError) = await EnsureChromeAsync(cancellationToken).ConfigureAwait(false);
            if (!port.HasValue)
                return TikTokUploadResult.Failed(chromeError ?? "تعذر الاتصال بـChrome.");

            StatusChanged?.Invoke("Chrome متصل. افتح حساب TikTok في النافذة عند طلب تسجيل الدخول.");
            DateTime deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ChromeTarget? target = await FindTikTokTargetAsync(port.Value, cancellationToken).ConfigureAwait(false);
                    if (target != null)
                    {
                        await using var cdp = await ChromeDevToolsClient.ConnectAsync(target.WebSocketDebuggerUrl, cancellationToken).ConfigureAwait(false);
                        await cdp.SendAsync("DOM.enable", new { }, cancellationToken).ConfigureAwait(false);
                        await cdp.SendAsync("Runtime.enable", new { }, cancellationToken).ConfigureAwait(false);
                        if (await TrySetFileInputAsync(cdp, videoPath, cancellationToken).ConfigureAwait(false))
                        {
                            StatusChanged?.Invoke("بدأ TikTok Studio رفع الفيديو عبر Chrome...");
                            await Task.Delay(1800, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(caption))
                                await TrySetCaptionAsync(cdp, caption, cancellationToken).ConfigureAwait(false);

                            return TikTokUploadResult.Succeeded(
                                "تم تسليم الفيديو إلى TikTok Studio وبدأ الرفع. راجع الغلاف والوصف وإعدادات الخصوصية داخل Chrome ثم اضغط نشر.");
                        }
                    }
                }
                catch (WebSocketException)
                {
                    // TikTok may replace the page target after login; reconnect on the next pass.
                }
                catch (HttpRequestException)
                {
                    // Chrome can briefly stop exposing the target during navigation.
                }

                StatusChanged?.Invoke("بانتظار صفحة الرفع... سجّل الدخول أو أكمل التحقق في Chrome إن ظهر.");
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }

            return TikTokUploadResult.Failed(
                "لم يظهر حقل رفع TikTok خلال ثلاث دقائق. أكمل تسجيل الدخول في Chrome ثم اضغط «متابعة الرفع».");
        }

        private async Task<(int? Port, string? Error)> EnsureChromeAsync(CancellationToken cancellationToken)
        {
            string? chromePath = FindChromeExecutable();
            if (chromePath == null)
                return (null, "لم يتم العثور على Google Chrome. ثبّت Chrome ثم أعد المحاولة.");

            Directory.CreateDirectory(_profileDirectory);
            StatusChanged?.Invoke("تشغيل Chrome بملف ForgeFlow الآمن...");
            StartChrome(chromePath);
            int? port = await WaitForDevToolsPortAsync(cancellationToken).ConfigureAwait(false);
            return port.HasValue
                ? (port, null)
                : (null, "تعذر تفعيل وضع الرفع. أغلق جميع نوافذ Chrome الخاصة بـForgeFlow بعد تسجيل الدخول ثم أعد المحاولة.");
        }

        private void StartLoginChrome(string chromePath)
        {
            var start = new ProcessStartInfo(chromePath)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            };
            start.ArgumentList.Add("--user-data-dir=" + _profileDirectory);
            start.ArgumentList.Add("--profile-directory=Default");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add(UploadUrl);
            Process.Start(start);
        }

        private async Task CloseDebugChromeIfRunningAsync(CancellationToken cancellationToken)
        {
            string activePortFile = Path.Combine(_profileDirectory, "DevToolsActivePort");
            try
            {
                if (!File.Exists(activePortFile))
                    return;

                string? firstLine = (await File.ReadAllLinesAsync(activePortFile, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                if (!int.TryParse(firstLine, out int port) || !await IsDevToolsReadyAsync(port, cancellationToken).ConfigureAwait(false))
                    return;

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("webSocketDebuggerUrl", out JsonElement websocketElement))
                    return;
                string? websocket = websocketElement.GetString();
                if (string.IsNullOrWhiteSpace(websocket))
                    return;

                await using var cdp = await ChromeDevToolsClient.ConnectAsync(websocket, cancellationToken).ConfigureAwait(false);
                await cdp.SendAsync("Browser.close", new { }, cancellationToken).ConfigureAwait(false);
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Login mode can still be launched manually if an old debug session is already gone.
            }
        }

        private void StartChrome(string chromePath)
        {
            var start = new ProcessStartInfo(chromePath)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            };
            start.ArgumentList.Add("--remote-debugging-port=0");
            start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            start.ArgumentList.Add("--user-data-dir=" + _profileDirectory);
            start.ArgumentList.Add("--profile-directory=Default");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add(UploadUrl);
            Process.Start(start);
        }

        private async Task<int?> WaitForDevToolsPortAsync(CancellationToken cancellationToken)
        {
            string activePortFile = Path.Combine(_profileDirectory, "DevToolsActivePort");
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(activePortFile))
                    {
                        string? firstLine = (await File.ReadAllLinesAsync(activePortFile, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                        if (int.TryParse(firstLine, out int port) && await IsDevToolsReadyAsync(port, cancellationToken).ConfigureAwait(false))
                            return port;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<bool> IsDevToolsReadyAsync(int port, CancellationToken cancellationToken)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static async Task<ChromeTarget?> FindTikTokTargetAsync(int port, CancellationToken cancellationToken)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (!string.Equals(item.GetProperty("type").GetString(), "page", StringComparison.OrdinalIgnoreCase))
                    continue;
                string url = item.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() ?? "" : "";
                string websocket = item.TryGetProperty("webSocketDebuggerUrl", out JsonElement wsElement) ? wsElement.GetString() ?? "" : "";
                if (url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(websocket))
                    return new ChromeTarget(websocket);
            }
            return null;
        }

        private static async Task<bool> TrySetFileInputAsync(ChromeDevToolsClient cdp, string videoPath, CancellationToken cancellationToken)
        {
            JsonElement result = await cdp.SendAsync("DOM.getFlattenedDocument", new { depth = -1, pierce = true }, cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("result", out JsonElement commandResult) ||
                !commandResult.TryGetProperty("nodes", out JsonElement nodes))
                return false;

            foreach (JsonElement node in nodes.EnumerateArray())
            {
                string localName = node.TryGetProperty("localName", out JsonElement local) ? local.GetString() ?? "" : "";
                if (!string.Equals(localName, "input", StringComparison.OrdinalIgnoreCase))
                    continue;

                string attributes = node.TryGetProperty("attributes", out JsonElement attrs)
                    ? string.Join(" ", attrs.EnumerateArray().Select(x => x.GetString() ?? ""))
                    : "";
                if (!attributes.Contains("file", StringComparison.OrdinalIgnoreCase) &&
                    !attributes.Contains("video", StringComparison.OrdinalIgnoreCase))
                    continue;

                int backendNodeId = node.GetProperty("backendNodeId").GetInt32();
                await cdp.SendAsync("DOM.setFileInputFiles", new { files = new[] { Path.GetFullPath(videoPath) }, backendNodeId }, cancellationToken).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        private static async Task TrySetCaptionAsync(ChromeDevToolsClient cdp, string caption, CancellationToken cancellationToken)
        {
            string captionJson = JsonSerializer.Serialize(caption);
            string expression = $$"""
                (() => {
                  const value = {{captionJson}};
                  const nodes = [...document.querySelectorAll('textarea,[contenteditable="true"]')];
                  const target = nodes.find(e => /caption|description|وصف|تعليق/i.test((e.getAttribute('aria-label')||'')+' '+(e.getAttribute('data-e2e')||''))) || nodes[0];
                  if (!target) return false;
                  target.focus();
                  if (target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement) {
                    const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(target), 'value')?.set;
                    setter ? setter.call(target, value) : target.value = value;
                  } else {
                    target.textContent = value;
                  }
                  target.dispatchEvent(new InputEvent('input', {bubbles:true, inputType:'insertText', data:value}));
                  target.dispatchEvent(new Event('change', {bubbles:true}));
                  return true;
                })()
                """;
            await cdp.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, cancellationToken).ConfigureAwait(false);
        }

        private static string? FindChromeExecutable()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private sealed record ChromeTarget(string WebSocketDebuggerUrl);
    }

    internal sealed record TikTokUploadResult(bool Success, string Message)
    {
        public static TikTokUploadResult Succeeded(string message) => new(true, message);
        public static TikTokUploadResult Failed(string message) => new(false, message);
    }

    internal sealed class ChromeDevToolsClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _nextId;

        private ChromeDevToolsClient() { }

        public static async Task<ChromeDevToolsClient> ConnectAsync(string websocketUrl, CancellationToken cancellationToken)
        {
            var client = new ChromeDevToolsClient();
            await client._socket.ConnectAsync(new Uri(websocketUrl), cancellationToken).ConfigureAwait(false);
            return client;
        }

        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref _nextId);
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters }));
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                using var stream = new MemoryStream();
                var buffer = new byte[32 * 1024];
                WebSocketReceiveResult receive;
                do
                {
                    receive = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (receive.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Chrome DevTools closed the connection.");
                    stream.Write(buffer, 0, receive.Count);
                }
                while (!receive.EndOfMessage);

                using JsonDocument document = JsonDocument.Parse(stream.ToArray());
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("id", out JsonElement responseId) && responseId.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out JsonElement error))
                        throw new InvalidOperationException("Chrome DevTools: " + error.ToString());
                    return root.Clone();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            _socket.Dispose();
        }
    }
}
