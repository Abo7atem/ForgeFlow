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

        private enum UploadState
        {
            WaitingForInput,
            FileSelected,
            Uploading,
            Ready,
            Failed,
            TimedOut
        }

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
                "تم فتح Chrome الخاص بـForgeFlow. سجّل الدخول إلى TikTok، ويمكنك إبقاء النافذة مفتوحة ثم الضغط «رفع عبر Chrome» مباشرة.");
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
            DateTime lastUploadNavigationUtc = DateTime.MinValue;
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
                            StatusChanged?.Invoke("تم اختيار الملف. التحقق من بدء الرفع الفعلي داخل TikTok...");
                            // TikTok can render the upload options asynchronously after the file is selected.
                            // Give the page a short window to create the quality controls before we inspect them.
                            UploadState uploadState = await WaitForUploadStateAsync(cdp, cancellationToken).ConfigureAwait(false);
                            if (uploadState == UploadState.Failed)
                                return TikTokUploadResult.Failed("رفض TikTok الملف أو أبلغ عن خطأ أثناء الرفع. راجع Chrome ثم أعد المحاولة.");
                            if (uploadState == UploadState.TimedOut)
                                return TikTokUploadResult.Succeeded("تم اختيار الملف، لكن Chrome لم يؤكد اكتمال الرفع ضمن المهلة. تحقق من شريط التقدم وظهور المعاينة قبل النشر.");

                            StatusChanged?.Invoke(uploadState == UploadState.Ready
                                ? "أكد TikTok جاهزية الفيديو داخل Chrome؛ جارٍ تطبيق إعدادات الجودة المتاحة."
                                : "بدأ TikTok Studio رفع الفيديو عبر Chrome...");
                            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                            bool hdEnabled = await TryEnableHighQualityUploadAsync(cdp, cancellationToken).ConfigureAwait(false);
                            StatusChanged?.Invoke(hdEnabled
                                ? "تم تفعيل خيار الجودة العالية في TikTok (إن كان متاحًا للحساب/المنطقة)."
                                : "لم يظهر خيار HD صراحةً؛ سيكمل TikTok المعالجة بأعلى جودة يسمح بها الحساب.");
                            if (!string.IsNullOrWhiteSpace(caption))
                                await TrySetCaptionAsync(cdp, caption, cancellationToken).ConfigureAwait(false);

                            return TikTokUploadResult.Succeeded(
                                "تم تسليم الفيديو إلى TikTok Studio وبدأ الرفع. راجع الغلاف والوصف وإعدادات الخصوصية داخل Chrome ثم اضغط نشر.");
                        }

                        // TikTok sometimes leaves the previous completed upload mounted at the
                        // /upload URL and removes its file input. Force a fresh Studio upload
                        // route, but throttle navigation so the SPA has time to mount the input.
                        if (!target.Url.Contains("/upload", StringComparison.OrdinalIgnoreCase) ||
                            DateTime.UtcNow - lastUploadNavigationUtc > TimeSpan.FromSeconds(10))
                        {
                            StatusChanged?.Invoke("فتح صفحة رفع TikTok Studio داخل Chrome...");
                            string freshUploadUrl = UploadUrl + "&forgeflow=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            lastUploadNavigationUtc = DateTime.UtcNow;
                            await cdp.SendAsync("Page.navigate", new { url = freshUploadUrl }, cancellationToken).ConfigureAwait(false);
                            await Task.Delay(2200, cancellationToken).ConfigureAwait(false);
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

        private static async Task<UploadState> WaitForUploadStateAsync(ChromeDevToolsClient cdp, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(75);
            UploadState previous = UploadState.FileSelected;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    const string expression = """
                        (() => {
                          const body = (document.body?.innerText || '').replace(/\\s+/g, ' ').trim();
                          const error = /(upload failed|couldn't upload|can't upload|upload error|فشل الرفع|تعذر الرفع|خطأ في الرفع|unsupported format|صيغة غير مدعومة)/i.test(body);
                          if (error) return 'failed';
                          const fileInput = document.querySelector('input[type=file]');
                          const selected = !!(fileInput && fileInput.files && fileInput.files.length);
                          const video = document.querySelector('video');
                          const hasPreview = !!(video && video.readyState >= 2 && Number.isFinite(video.duration) && video.duration > 0);
                          const progress = document.querySelector('[role="progressbar"],[aria-valuenow]');
                          const uploading = !!progress || /(uploading|processing|preparing|جار|جاري|معالجة|تحضير|٪|%)/i.test(body);
                          const readyText = /(uploaded|ready to post|cover|thumbnail|publish|تم الرفع|جاهز للنشر|الغلاف|نشر)/i.test(body);
                          if (hasPreview && readyText && !uploading) return 'ready';
                          if (uploading || (selected && !readyText)) return 'uploading';
                          return selected ? 'selected' : 'waiting';
                        })()
                        """;
                    JsonElement response = await cdp.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, cancellationToken).ConfigureAwait(false);
                    string state = response.GetProperty("result").GetProperty("result").GetProperty("value").GetString() ?? "waiting";
                    previous = state switch
                    {
                        "failed" => UploadState.Failed,
                        "ready" => UploadState.Ready,
                        "uploading" => UploadState.Uploading,
                        "selected" => UploadState.FileSelected,
                        _ => UploadState.WaitingForInput
                    };
                    if (previous is UploadState.Failed or UploadState.Ready)
                        return previous;
                }
                catch (WebSocketException)
                {
                    return UploadState.Failed;
                }
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            }
            return UploadState.TimedOut;
        }

        private async Task<(int? Port, string? Error)> EnsureChromeAsync(CancellationToken cancellationToken)
        {
            string? chromePath = FindChromeExecutable();
            if (chromePath == null)
                return (null, "لم يتم العثور على Google Chrome. ثبّت Chrome ثم أعد المحاولة.");

            Directory.CreateDirectory(_profileDirectory);
            int? existingPort = await TryGetExistingDevToolsPortAsync(cancellationToken).ConfigureAwait(false);
            if (existingPort.HasValue)
            {
                StatusChanged?.Invoke("تم العثور على جلسة Chrome الخاصة بـForgeFlow وإعادة استخدامها...");
                return (existingPort, null);
            }

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
            start.ArgumentList.Add("--remote-debugging-port=0");
            start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
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

        private async Task<int?> TryGetExistingDevToolsPortAsync(CancellationToken cancellationToken)
        {
            string activePortFile = Path.Combine(_profileDirectory, "DevToolsActivePort");
            try
            {
                if (!File.Exists(activePortFile))
                    return null;

                string? firstLine = (await File.ReadAllLinesAsync(activePortFile, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                if (int.TryParse(firstLine, out int port) && await IsDevToolsReadyAsync(port, cancellationToken).ConfigureAwait(false))
                    return port;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
            ChromeTarget? fallback = null;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (!string.Equals(item.GetProperty("type").GetString(), "page", StringComparison.OrdinalIgnoreCase))
                    continue;
                string url = item.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() ?? "" : "";
                string websocket = item.TryGetProperty("webSocketDebuggerUrl", out JsonElement wsElement) ? wsElement.GetString() ?? "" : "";
                if (!url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(websocket))
                    continue;

                // Prefer the Studio upload page. A login/profile tab can otherwise win the race
                // and has no <input type=file> even though Chrome is already authenticated.
                var candidate = new ChromeTarget(websocket, url);
                if (url.Contains("tiktokstudio/upload", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("upload", StringComparison.OrdinalIgnoreCase))
                    return candidate;
                fallback ??= candidate;
            }
            return fallback;
        }

        private static async Task<bool> TrySetFileInputAsync(ChromeDevToolsClient cdp, string videoPath, CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(videoPath);

            // Prefer a live nodeId from the current document. TikTok frequently replaces the
            // upload control after navigation; stale backendNodeIds then produce
            // "Node is not a file input element" even when the original node was correct.
            try
            {
                JsonElement document = await cdp.SendAsync("DOM.getDocument", new { depth = 1, pierce = true }, cancellationToken).ConfigureAwait(false);
                int rootNodeId = document.GetProperty("result").GetProperty("root").GetProperty("nodeId").GetInt32();
                JsonElement query = await cdp.SendAsync("DOM.querySelector", new { nodeId = rootNodeId, selector = "input[type=file]" }, cancellationToken).ConfigureAwait(false);
                int nodeId = query.GetProperty("result").GetProperty("nodeId").GetInt32();
                if (nodeId > 0)
                {
                    await cdp.SendAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, nodeId }, cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The page may have replaced the input between query and set; use the
                // flattened-document fallback below and retry on the next outer pass.
            }

            JsonElement result = await cdp.SendAsync("DOM.getFlattenedDocument", new { depth = -1, pierce = true }, cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("result", out JsonElement commandResult) ||
                !commandResult.TryGetProperty("nodes", out JsonElement nodes))
                return false;

            foreach (JsonElement node in nodes.EnumerateArray())
            {
                string localName = node.TryGetProperty("localName", out JsonElement local) ? local.GetString() ?? "" : "";
                if (!string.Equals(localName, "input", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? type = null;
                if (node.TryGetProperty("attributes", out JsonElement attrs))
                {
                    string[] values = attrs.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                    for (int i = 0; i + 1 < values.Length; i += 2)
                    {
                        if (string.Equals(values[i], "type", StringComparison.OrdinalIgnoreCase))
                        {
                            type = values[i + 1];
                            break;
                        }
                    }
                }
                if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (node.TryGetProperty("nodeId", out JsonElement nodeIdElement) && nodeIdElement.GetInt32() > 0)
                    {
                        int nodeId = nodeIdElement.GetInt32();
                        await cdp.SendAsync("DOM.setFileInputFiles", new { files = new[] { fullPath }, nodeId }, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Stale node; keep scanning for the current file input.
                }
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

        private static async Task<bool> TryEnableHighQualityUploadAsync(ChromeDevToolsClient cdp, CancellationToken cancellationToken)
        {
            // TikTok changes its Studio DOM frequently. Look only for explicit quality controls;
            // never click arbitrary buttons, so the user still reviews privacy and publishes manually.
            await TryExpandMoreUploadOptionsAsync(cdp, cancellationToken).ConfigureAwait(false);

            const string expression = """
                (() => {
                  const quality = /(upload\s*(hd|high quality)|high\s*quality|1080p|4k|تحميلات?\s+عالية\s+الجودة|عالية\s+الجودة|جودة\s+عالية|رفع.*جودة|hd)/i;
                  const candidates = [...document.querySelectorAll('input[type="checkbox"], [role="checkbox"], [role="switch"], button, label, [aria-label]')];
                  for (const element of candidates) {
                    const text = [element.innerText, element.textContent, element.getAttribute('aria-label'), element.getAttribute('data-e2e')]
                      .filter(Boolean).join(' ').replace(/\s+/g, ' ').trim();
                    if (!quality.test(text)) continue;
                    const input = element.matches('input[type="checkbox"]') ? element : element.querySelector('input[type="checkbox"]');
                    const roleState = element.getAttribute('aria-checked');
                    const pressed = element.getAttribute('aria-pressed');
                    const dataState = element.getAttribute('data-state');
                    const checked = input ? input.checked : roleState === 'true' || pressed === 'true' || dataState === 'checked' || dataState === 'on';
                    if (checked) return true;
                    element.click();
                    return true;
                  }
                  return false;
                })()
                """;

            try
            {
                JsonElement response = await cdp.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, cancellationToken).ConfigureAwait(false);
                return response.TryGetProperty("result", out JsonElement result) &&
                       result.TryGetProperty("result", out JsonElement value) &&
                       value.TryGetProperty("value", out JsonElement enabled) &&
                       enabled.ValueKind == JsonValueKind.True;
            }
            catch
            {
                // A DOM refresh or a TikTok experiment can close the target between reads.
                return false;
            }
        }

        private static async Task TryExpandMoreUploadOptionsAsync(ChromeDevToolsClient cdp, CancellationToken cancellationToken)
        {
            const string expression = """
                (() => {
                  const more = /(show\s+more|more\s+options|إظهار\s+المزيد|المزيد)/i;
                  const nodes = [...document.querySelectorAll('button, [role="button"], [aria-label]')];
                  const target = nodes.find(e => more.test([e.innerText, e.textContent, e.getAttribute('aria-label')].filter(Boolean).join(' ').replace(/\s+/g, ' ').trim()));
                  if (!target) return false;
                  target.click();
                  return true;
                })()
                """;
            try
            {
                await cdp.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, cancellationToken).ConfigureAwait(false);
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Optional accordion; its absence is normal for some TikTok accounts.
            }
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

        private sealed record ChromeTarget(string WebSocketDebuggerUrl, string Url);
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
