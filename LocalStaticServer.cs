using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MapleMonitor
{
    public sealed class LocalStaticServer : IDisposable
    {
        private static readonly Regex VersionedAsset = new Regex(@"\.[0-9a-f]{12,64}\.[^.]+$", RegexOptions.IgnoreCase);
        private const string RemotePushPath = "/api/remote-push";
        private readonly string root;
        private readonly string instanceId;
        private TcpListener listener;
        private int port;
        private int clientCount;

        public LocalStaticServer(string directory, string instanceId)
        {
            root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            this.instanceId = instanceId;
        }

        public int Start(int firstPort, int lastPort)
        {
            for (port = firstPort; port <= lastPort; port++)
            {
                var candidate = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    candidate.Start();
                    listener = candidate;
                    new Thread(AcceptClients) { IsBackground = true }.Start();
                    return port;
                }
                catch (SocketException)
                {
                    candidate.Stop();
                }
            }
            throw new IOException("No free local port was found in " + firstPort + "-" + lastPort + ".");
        }

        private void AcceptClients()
        {
            try
            {
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    if (Interlocked.Increment(ref clientCount) <= 32)
                    {
                        ThreadPool.QueueUserWorkItem(HandleClient, client);
                    }
                    else
                    {
                        client.Close();
                        Interlocked.Decrement(ref clientCount);
                    }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { } // Listener stopped.
        }

        private void HandleClient(object state)
        {
            try
            {
                using (var client = (TcpClient)state)
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 15000;
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        Respond(stream, reader);
                    }
                }
            }
            catch (IOException) { } // Closed, timed-out or canceled by the browser.
            catch (SocketException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                Interlocked.Decrement(ref clientCount);
            }
        }

        private void Respond(Stream stream, StreamReader reader)
        {
            var firstLine = ReadLine(reader, 8192);
            if (string.IsNullOrEmpty(firstLine)) return;
            var parts = firstLine.Split(' ');
            var head = parts[0] == "HEAD";
            if (parts.Length != 3 || !parts[1].StartsWith("/", StringComparison.Ordinal))
            {
                SendHeaders(stream, "400 Bad Request", "text/plain", 0, "no-store");
                return;
            }
            var path = parts[1].Split('?')[0];
            if (path != RemotePushPath && !head && parts[0] != "GET")
            {
                SendHeaders(stream, "405 Method Not Allowed", "text/plain", 0, "no-store", "Allow: GET, HEAD\r\n");
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var remaining = 16384 - firstLine.Length;
            while (true)
            {
                var line = ReadLine(reader, remaining);
                if (line == null) return;
                if (line.Length == 0) break;
                remaining -= line.Length + 2;
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            string host;
            if (!headers.TryGetValue("Host", out host) ||
                (!host.Equals("127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) &&
                 !host.Equals("localhost:" + port, StringComparison.OrdinalIgnoreCase)))
            {
                SendHeaders(stream, "400 Bad Request", "text/plain", 0, "no-store");
                return;
            }

            if (path == RemotePushPath)
            {
                if (parts[1] != RemotePushPath)
                {
                    SendPushResult(stream, "400 Bad Request", "远程提醒接口不接受查询参数。", head: head);
                    return;
                }
                RespondRemotePush(stream, reader, headers, host, parts[0]);
                return;
            }
            if (path == "/api/manbo-voice")
            {
                RespondManbo(stream, parts[1], headers, head);
                return;
            }
            if (path == "/api/voice-cache")
            {
                RespondVoiceCache(stream, parts[1], headers, head);
                return;
            }
            if (path == "/.well-known/maple-monitor")
            {
                SendHeaders(stream, "200 OK", "text/plain", 0, "no-store",
                    "X-Maple-Monitor-Instance: " + instanceId + "\r\n");
                return;
            }

            var fullPath = ResolvePath(path);
            var mime = fullPath == null ? null : GetContentType(Path.GetExtension(fullPath).ToLowerInvariant());
            if (mime == null || !File.Exists(fullPath))
            {
                SendHeaders(stream, "404 Not Found", "text/plain", 0, "no-store");
                return;
            }

            string acceptEncoding;
            headers.TryGetValue("Accept-Encoding", out acceptEncoding);
            var encoding = PreferredEncoding(fullPath, acceptEncoding);
            var vary = File.Exists(fullPath + ".br") || File.Exists(fullPath + ".gz");
            var extra = vary ? "Vary: Accept-Encoding\r\n" : "";
            if (encoding != null)
            {
                fullPath += encoding == "br" ? ".br" : ".gz";
                extra += "Content-Encoding: " + encoding + "\r\n";
            }
            var cache = VersionedAsset.IsMatch(path) ? "public, max-age=31536000, immutable" : "no-store";
            using (var asset = File.OpenRead(fullPath))
            {
                SendHeaders(stream, "200 OK", mime, asset.Length, cache, extra);
                if (!head) asset.CopyTo(stream);
            }
        }

        private static void RespondRemotePush(Stream stream, StreamReader reader,
            Dictionary<string, string> headers, string host, string method)
        {
            if (method != "GET" && method != "POST")
            {
                SendPushResult(stream, "405 Method Not Allowed", "远程提醒接口只接受 GET 和 POST 请求。",
                    "Allow: GET, POST\r\n", method == "HEAD");
                return;
            }

            string gate, origin, site;
            if (!headers.TryGetValue("X-Maple-Push", out gate) || gate != "1" ||
                (headers.TryGetValue("Origin", out origin) && !origin.Equals("http://" + host, StringComparison.OrdinalIgnoreCase)) ||
                (headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin"))
            {
                SendPushResult(stream, "403 Forbidden", "请从本地监控页面发送远程提醒。");
                return;
            }
            if (method == "GET")
            {
                SendJson(stream, "200 OK", new { ok = true, capability = "remote-push" });
                return;
            }

            string contentType, contentLength;
            int length;
            if (!headers.TryGetValue("Content-Type", out contentType) ||
                !contentType.Split(';')[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
                headers.ContainsKey("Transfer-Encoding") ||
                !headers.TryGetValue("Content-Length", out contentLength) ||
                !int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                length <= 0 || length > 16384)
            {
                SendPushResult(stream, "400 Bad Request", "远程提醒请求格式不正确。");
                return;
            }

            var encoded = new char[length];
            if (reader.ReadBlock(encoded, 0, length) != length)
            {
                SendPushResult(stream, "400 Bad Request", "远程提醒请求不完整。");
                return;
            }
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in new string(encoded).Split('&'))
            {
                var equals = field.IndexOf('=');
                if (equals <= 0 || form.ContainsKey(field.Substring(0, equals)))
                {
                    SendPushResult(stream, "400 Bad Request", "远程提醒请求格式不正确。");
                    return;
                }
                form.Add(field.Substring(0, equals), DecodeQueryValue(field.Substring(equals + 1)));
            }

            string provider, token, channel, target, title, content;
            form.TryGetValue("provider", out provider);
            form.TryGetValue("token", out token);
            form.TryGetValue("channel", out channel);
            form.TryGetValue("target", out target);
            form.TryGetValue("title", out title);
            form.TryGetValue("content", out content);
            if (string.IsNullOrEmpty(token) || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{1,512}\z") ||
                string.IsNullOrEmpty(title) || title.Length > 200 || title.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                string.IsNullOrEmpty(content) || content.Length > 4000)
            {
                SendPushResult(stream, "400 Bad Request", "请检查推送密钥、标题和消息正文。");
                return;
            }

            string url;
            string service;
            var payload = new Dictionary<string, string>();
            switch (provider)
            {
                case "pushplus":
                    service = "PushPlus";
                    url = "https://www.pushplus.plus/send";
                    if (string.IsNullOrEmpty(channel)) channel = "wechat";
                    if (channel != "wechat" && channel != "qq" && channel != "app" &&
                        channel != "cmcc" && channel != "clawbot" && channel != "sms")
                    {
                        SendPushResult(stream, "400 Bad Request", "请选择受支持的 PushPlus 渠道。");
                        return;
                    }
                    payload["token"] = token;
                    payload["title"] = title;
                    payload["content"] = content;
                    payload["channel"] = channel;
                    payload["template"] = "txt";
                    if (channel == "qq" && !string.IsNullOrEmpty(target))
                    {
                        if (target.Length > 128 || target.IndexOf(',') >= 0 || Regex.IsMatch(target, @"\p{Cc}"))
                        {
                            SendPushResult(stream, "400 Bad Request", "请填写正确的 PushPlus QQ 配置编码。");
                            return;
                        }
                        payload["option"] = target;
                    }
                    break;
                case "qmsg":
                    service = "Qmsg酱";
                    url = "https://qmsg.zendee.cn/v3/jsend/" + token;
                    var message = title + "\n" + content;
                    if (message.Length > 1800)
                    {
                        SendPushResult(stream, "400 Bad Request", "Qmsg酱的标题和正文合计不能超过 1800 字符。");
                        return;
                    }
                    payload["msg"] = message;
                    if (!string.IsNullOrEmpty(target))
                    {
                        if (!Regex.IsMatch(target, @"\A[0-9]{1,20}\z"))
                        {
                            SendPushResult(stream, "400 Bad Request", "请输入数字格式的 QQ 群号。");
                            return;
                        }
                        payload["group"] = target;
                    }
                    break;
                case "serverchan":
                    service = "Server酱 Turbo";
                    url = "https://sctapi.ftqq.com/" + token + ".send";
                    payload["title"] = title;
                    payload["desp"] = content;
                    break;
                default:
                    SendPushResult(stream, "400 Bad Request", "请选择受支持的远程提醒服务。");
                    return;
            }

            var error = ForwardRemotePush(url, provider, service, payload);
            SendPushResult(stream, error == null ? "200 OK" : "502 Bad Gateway", error);
        }

        private static string ForwardRemotePush(string url, string provider, string service, Dictionary<string, string> payload)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var bytes = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
                var request = WebRequest.CreateHttp(url);
                request.AllowAutoRedirect = false;
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = bytes.Length;
                request.Timeout = request.ReadWriteTimeout = 5000;
                using (var target = request.GetRequestStream()) target.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var buffer = new char[8193];
                    var length = reader.ReadBlock(buffer, 0, buffer.Length);
                    if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300 && length < buffer.Length)
                    {
                        var result = serializer.DeserializeObject(new string(buffer, 0, length)) as Dictionary<string, object>;
                        string key = provider == "qmsg" ? "success" : "code";
                        object success = provider == "qmsg" ? (object)true : provider == "pushplus" ? 200 : 0;
                        object value;
                        if (result != null && result.TryGetValue(key, out value) && object.Equals(value, success)) return null;
                    }
                }
                return service + "未接受推送，请检查密钥、渠道绑定和可用额度。";
            }
            catch (WebException error)
            {
                if (error.Status == WebExceptionStatus.Timeout)
                    return service + "响应超时，推送结果未确认。";
                if (error.Response != null)
                {
                    error.Response.Dispose();
                    return service + "未接受推送，请检查密钥、渠道绑定和可用额度。";
                }
                return "无法连接 " + service + "，请检查网络后重试。";
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            return "无法读取 " + service + " 的推送结果，请稍后检查服务状态。";
        }

        private static void SendPushResult(Stream stream, string status, string error, string extra = "", bool head = false)
        {
            var body = new Dictionary<string, object> { { "ok", error == null } };
            if (error != null) body["message"] = error;
            SendJson(stream, status, body, extra, head);
        }

        private static void SendJson(Stream stream, string status, object body, string extra = "", bool head = false)
        {
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
            SendHeaders(stream, status, "application/json; charset=utf-8", bytes.Length, "no-store", extra);
            if (!head) stream.Write(bytes, 0, bytes.Length);
        }

        private static bool AllowsVoiceRequest(Dictionary<string, string> headers, bool head)
        {
            string gate;
            string site;
            return !head && headers.TryGetValue("X-Maple-Speech", out gate) && gate == "1" &&
                (!headers.TryGetValue("Sec-Fetch-Site", out site) || site == "same-origin");
        }

        private static string DecodeQueryValue(string value)
        {
            return Uri.UnescapeDataString(value.Replace('+', ' ')).Trim();
        }

        private static void RespondManbo(Stream stream, string target, Dictionary<string, string> headers, bool head)
        {
            if (!AllowsVoiceRequest(headers, head))
            {
                SendHeaders(stream, "403 Forbidden", "text/plain", 0, "no-store");
                return;
            }
            const string prefix = "/api/manbo-voice?text=";
            var text = target.StartsWith(prefix, StringComparison.Ordinal) && target.IndexOf('&') < 0
                ? DecodeQueryValue(target.Substring(prefix.Length)) : "";
            if (text.Length == 0 || text.Length > 300)
            {
                SendHeaders(stream, "400 Bad Request", "text/plain", 0, "no-store");
                return;
            }
            var token = ManboToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                SendHeaders(stream, "503 Service Unavailable", "text/plain", 0, "no-store");
                return;
            }
            var audioUrl = ResolveManbo(text, token);
            if (audioUrl == null)
            {
                SendHeaders(stream, "502 Bad Gateway", "text/plain", 0, "no-store");
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(audioUrl);
            SendHeaders(stream, "200 OK", "text/plain; charset=utf-8", bytes.Length, "no-store");
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string ManboToken()
        {
            const string name = "MAPLE_MONITOR_MANBO_TOKEN";
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(name);
        }

        private static HttpWebRequest VoiceRequest(string url)
        {
            var request = WebRequest.CreateHttp(url);
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 4200;
            return request;
        }

        private static string ResolveManbo(string text, string token)
        {
            try
            {
                var request = VoiceRequest("https://apicx.asia/api/get_tts_audio?material=" + Uri.EscapeDataString("曼波")
                    + "&text=" + Uri.EscapeDataString(text) + "&token=" + Uri.EscapeDataString(token.Trim()));
                request.Headers["Authorization"] = token.Trim();
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var source = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var buffer = new char[8193];
                    var length = source.ReadBlock(buffer, 0, buffer.Length);
                    if (length < buffer.Length)
                    {
                        var payload = new JavaScriptSerializer().DeserializeObject(new string(buffer, 0, length)) as Dictionary<string, object>;
                        object code, data, value;
                        Uri url;
                        if (payload != null && payload.TryGetValue("code", out code) && object.Equals(code, 200) &&
                            payload.TryGetValue("data", out data) && data is Dictionary<string, object> &&
                            ((Dictionary<string, object>)data).TryGetValue("audio_url", out value) &&
                            Uri.TryCreate(value as string, UriKind.Absolute, out url) && url.Scheme == "https" &&
                            url.IsDefaultPort && url.UserInfo.Length == 0 && url.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase))
                            return url.AbsoluteUri;
                    }
                }
            }
            catch (WebException error) { if (error.Response != null) error.Response.Dispose(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            return null;
        }

        private static void RespondVoiceCache(Stream stream, string target, Dictionary<string, string> headers, bool head)
        {
            if (!AllowsVoiceRequest(headers, head))
            {
                SendHeaders(stream, "403 Forbidden", "text/plain", 0, "no-store");
                return;
            }
            const string prefix = "/api/voice-cache?voice=";
            var fields = target.StartsWith(prefix, StringComparison.Ordinal) ? target.Substring(prefix.Length).Split('&') : new string[0];
            var text = fields.Length == 2 && fields[1].StartsWith("text=", StringComparison.Ordinal)
                ? DecodeQueryValue(fields[1].Substring(5)) : "";
            var voice = fields.Length == 2 ? DecodeQueryValue(fields[0]) : "";
            string speaker;
            switch (voice)
            {
                case "online:manbo": speaker = null; break;
                case "online:cantonese": speaker = "zh-HK-HiuMaanNeural"; break;
                case "online:cantonese-male": speaker = "zh-HK-WanLungNeural"; break;
                case "online:mandarin": speaker = "zh-CN-XiaoxiaoNeural"; break;
                default: speaker = ""; break;
            }
            if (text.Length == 0 || text.Length > 120 || speaker == "")
            {
                SendHeaders(stream, "400 Bad Request", "text/plain", 0, "no-store");
                return;
            }
            string url;
            if (speaker == null)
            {
                var token = ManboToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    SendHeaders(stream, "503 Service Unavailable", "text/plain", 0, "no-store");
                    return;
                }
                url = ResolveManbo(text, token);
            }
            else url = "https://api.vsaa.cn/api/toolbox/tts/tts.php?raw=1&voice=" + speaker + "&text=" + Uri.EscapeDataString(text);

            var bytes = url == null ? null : DownloadVoice(url);
            var mime = AudioMime(bytes);
            if (mime == null)
            {
                SendHeaders(stream, "502 Bad Gateway", "text/plain", 0, "no-store");
                return;
            }
            SendHeaders(stream, "200 OK", mime, bytes.Length, "no-store");
            stream.Write(bytes, 0, bytes.Length);
        }

        private static byte[] DownloadVoice(string url)
        {
            const int limit = 2 * 1024 * 1024;
            try
            {
                var request = VoiceRequest(url);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var source = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > limit) return null;
                    var data = new byte[8192];
                    var deadline = DateTime.UtcNow.AddSeconds(4);
                    int length;
                    while ((length = source.Read(data, 0, data.Length)) > 0)
                    {
                        if (buffer.Length + length > limit || DateTime.UtcNow > deadline) return null;
                        buffer.Write(data, 0, length);
                    }
                    return buffer.ToArray();
                }
            }
            catch (WebException error) { if (error.Response != null) error.Response.Dispose(); }
            catch (IOException) { }
            return null;
        }

        private static string AudioMime(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 16) return null;
            if (Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE") return "audio/wav";
            if (Encoding.ASCII.GetString(bytes, 0, 3) == "ID3" || (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0)) return "audio/mpeg";
            return null;
        }

        private string ResolvePath(string path)
        {
            try
            {
                var relative = Uri.UnescapeDataString(path).TrimStart('/');
                if (relative.Length == 0) relative = "index.html";
                foreach (var segment in relative.Split('/', '\\'))
                {
                    if (segment.StartsWith(".", StringComparison.Ordinal)) return null;
                }
                var fullPath = Path.GetFullPath(Path.Combine(root, relative));
                return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static string ReadLine(StreamReader reader, int limit)
        {
            var line = new StringBuilder();
            while (line.Length < limit)
            {
                var character = reader.Read();
                if (character == -1) return null;
                if (character == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)character);
            }
            return null;
        }

        private static string PreferredEncoding(string path, string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            var qualities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in header.Split(','))
            {
                var parameters = value.Split(';');
                double quality = 1;
                if (parameters.Length > 1)
                {
                    var parameter = parameters[1].Trim();
                    if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase) ||
                        !double.TryParse(parameter.Substring(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality) ||
                        quality < 0 || quality > 1) continue;
                }
                qualities[parameters[0].Trim()] = quality;
            }
            double wildcard, brotli, gzip;
            qualities.TryGetValue("*", out wildcard);
            if (!qualities.TryGetValue("br", out brotli)) brotli = wildcard;
            if (!qualities.TryGetValue("gzip", out gzip)) gzip = wildcard;
            if (!File.Exists(path + ".br")) brotli = 0;
            if (!File.Exists(path + ".gz")) gzip = 0;
            if (brotli > 0 && brotli >= gzip) return "br";
            return gzip > 0 ? "gzip" : null;
        }

        private static string GetContentType(string extension)
        {
            switch (extension)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".wasm": return "application/wasm";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                default: return null;
            }
        }

        private static void SendHeaders(Stream stream, string status, string mime, long length, string cache, string extra = "")
        {
            var headers = "HTTP/1.1 " + status + "\r\nContent-Type: " + mime +
                "\r\nContent-Length: " + length + "\r\nCache-Control: " + cache +
                "\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n" + extra + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            if (listener != null) listener.Stop();
        }
    }
}
