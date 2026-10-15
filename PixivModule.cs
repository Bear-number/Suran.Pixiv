using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.Pixiv;

public class PixivConfig
{
    [DisplayName("协议端地址")]
    [Description("OneBot正向WebSocket地址，图片消息从这里发出去")]
    public string WsUrl { get; set; } = "ws://127.0.0.1:3001";

    [DisplayName("搜图·代理地址")]
    [Description("访问 pixiv.net 搜索接口要用的HTTP代理，如 http://127.0.0.1:7890。国内直连不通，按关键词搜图必填；只发已知作品ID不用填")]
    public string ProxyUrl { get; set; } = "";

    [DisplayName("图片目录")]
    [Description("相对 Alife Storage 的图片目录，同一个作品同一页不会重复下载")]
    public string ImageFolder { get; set; } = "Pixiv";

    [DisplayName("搜索超时(秒)")]
    [Description("一次搜索请求的超时，默认20")]
    public int SearchTimeoutSeconds { get; set; } = 20;

    [DisplayName("取图超时(秒)")]
    [Description("镜像给的是原图，体积可能十几MB，默认60")]
    public int DownloadTimeoutSeconds { get; set; } = 60;

    [DisplayName("开关·发送图片")]
    [Description("关闭后 PixivSend 不可用，也不会下载图片；PixivSearch 只查不发，不受影响")]
    public bool EnableSend { get; set; } = true;
}

[Module("P站图片搜索发送",
    "免登录搜 Pixiv 作品并把原图以QQ图片消息发出：关键词搜图取候选、按作品ID或链接直接发、多页作品取指定页",
    defaultCategory: "苏染的工具")]
public class PixivModule(
    XmlFunctionCaller functionCaller,
    ILogger<PixivModule> logger,
    Interactor<PixivModule> interactor
) : ChatBehaviour, IConfigurable<PixivConfig>
{
    public PixivConfig Configuration { get; set; } = null!;

    const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    const string PixivHomeUrl = "https://www.pixiv.net/";
    const string SearchBase = "https://www.pixiv.net/ajax/search/artworks/";
    const string IllustBase = "https://www.pixiv.net/ajax/illust/";
    const string MirrorBase = "https://pixiv.re/";
    const string TestKeyword = "初音ミク";
    const int MaxResults = 20;
    const int ProbeTimeoutSeconds = 8;

    static ClientWebSocket? sharedWs;
    static readonly SemaphoreSlim wsLock = new(1, 1);
    static string providerName = "";
    static bool providerResolved;

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "Pixiv 搜图并发图片：搜索作品候选、按关键词或作品ID把原图发到QQ群/私聊。",
            Explanation = "全程不用登录 Pixiv 账号。搜索走 pixiv.net 的公开接口，国内直连不通，需要在插件配置里填「搜图·代理地址」；"
                + "图片本身走 pixiv.re 镜像，国内直连就能取。发之前一般先用 PixivSearch 看候选（一次能回十几条，含尺寸页数标签），挑一条用 PixivSend 发；"
                + "用户直接给了作品ID或作品链接时不用搜索，也就不用代理。取不到图或发不出去先跑 PixivEnv。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜Pixiv作品（免登录，需要在配置里填代理）。返回作品ID、标题、作者、尺寸、页数、标签，供挑定后用 PixivSend 发送；order留空按人气排，填new按最新排")]
    public async Task PixivSearch(
        [Description("关键词，日文tag最准（如 初音ミク），中文和角色名也能搜")] string keyword,
        [Description("返回几条，1-20，默认8")] int count = 8,
        [Description("popular=人气（默认）/ new=最新")] string? order = null)
    {
        if (keyword.Trim().Length == 0) throw new Exception("没给关键词");
        List<PixivHit> hits = await SearchAsync(keyword, Math.Clamp(count, 1, MaxResults), order);
        if (hits.Count == 0)
        {
            interactor.Poke("Pixiv 搜「" + keyword + "」没有可发的结果（未登录接口会过滤R-18与私有作品，关键词换个说法或改日文tag试试）。");
            return;
        }
        List<string> lines = new()
        {
            "Pixiv 搜索「" + keyword + "」（" + (IsNewest(order) ? "最新" : "人气") + "，" + hits.Count + " 条）："
        };
        foreach (PixivHit hit in hits)
        {
            lines.Add("- " + hit.Id + " " + hit.Title + "／" + hit.UserName + "  " + hit.Width + "x" + hit.Height
                + (hit.PageCount > 1 ? " " + hit.PageCount + "P" : "")
                + (hit.IsAnimation ? " 动图(只能取静帧)" : "")
                + (hit.Tags.Length > 0 ? "  标签: " + hit.Tags : ""));
        }
        lines.Add("发图用 PixivSend(作品ID)，多P作品带 page（从1开始）；图片走 pixiv.re 镜像取原图，不需要代理。");
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从Pixiv取一张原图并以QQ图片消息发出（不是文件消息）。source可以是作品ID、作品链接，也可以是搜索关键词（关键词就发搜到的第一条人气结果，走关键词需要配置代理）；page是多页作品的第几页")]
    public async Task PixivSend(
        [Description("作品ID、pixiv作品链接，或搜索关键词")] string source,
        [Description("目标群号或QQ号")] long? targetId = null,
        [Description("group或private，默认group")] string? type = null,
        [Description("第几页，从1开始，默认1")] int page = 1)
    {
        if (Gate(Configuration.EnableSend, "发送图片")) return;
        if (targetId == null) throw new Exception("没给目标，发群里还是私聊得说一个");
        if (source.Trim().Length == 0) throw new Exception("没给关键词或作品ID");
        if (page < 1) throw new Exception("页码从1开始");
        bool isGroup = !string.Equals(type, "private", StringComparison.OrdinalIgnoreCase);

        PixivHit? hit = null;
        string id = ExtractArtworkId(source);
        if (id.Length == 0)
        {
            List<PixivHit> hits = await SearchAsync(source, 1, null);
            if (hits.Count == 0)
            {
                interactor.Poke("Pixiv 搜「" + source + "」没有可发的作品，换个关键词或用 PixivSearch 看候选。");
                return;
            }
            hit = hits[0];
            id = hit.Id;
        }
        else if (Configuration.ProxyUrl.Trim().Length > 0)
        {
            hit = await FetchHitAsync(id);
        }

        PixivFile file = await DownloadAsync(id, page);
        await SendAndReportAsync(hit, id, page, file, targetId.Value, isGroup);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自检：pixiv.net 直连与经代理通不通、搜索接口能不能用、pixiv.re 镜像通不通、图片目录缓存了多少、协议端是哪个端")]
    public async Task PixivEnv()
    {
        List<string> lines = new();
        string proxy = Configuration.ProxyUrl.Trim();
        lines.Add("pixiv.net 直连：" + await ProbeAsync(PixivHomeUrl, ""));
        if (proxy.Length == 0)
        {
            lines.Add("搜图·代理：未配置，按关键词搜图会失败（只发已知作品ID不受影响）");
        }
        else
        {
            lines.Add("pixiv.net 经代理（" + proxy + "）：" + await ProbeAsync(PixivHomeUrl, proxy));
            try
            {
                List<PixivHit> hits = await SearchAsync(TestKeyword, 3, null);
                lines.Add("搜索接口：可用，试搜「" + TestKeyword + "」拿到 " + hits.Count + " 条，第一条 "
                    + hits[0].Id + " " + hits[0].Title);
            }
            catch (Exception searchError)
            {
                lines.Add("❌ 搜索接口：" + searchError.Message);
            }
        }
        lines.Add("pixiv.re 镜像（直连取原图）：" + await ProbeAsync(MirrorBase, ""));
        lines.Add("图片目录：" + DescribeFolder());
        try
        {
            await ResolveProviderAsync();
            lines.Add("协议端：" + ProviderLabel() + "，地址 " + Configuration.WsUrl);
        }
        catch (Exception wsError)
        {
            lines.Add("❌ 协议端连不上（" + Configuration.WsUrl + "）：" + wsError.Message);
        }
        lines.Add("发送开关：" + (Configuration.EnableSend ? "开" : "关")
            + "，超时 搜索" + TimeoutSeconds(false) + "秒/取图" + TimeoutSeconds(true) + "秒");
        interactor.Poke(string.Join("\n", lines));
    }

    // ============================================================
    // 搜索
    // ============================================================

    sealed record PixivHit(string Id, string Title, string UserName, int Width, int Height, int PageCount, string Tags, bool IsAnimation);

    async Task<List<PixivHit>> SearchAsync(string keyword, int limit, string? order)
    {
        string proxy = Configuration.ProxyUrl.Trim();
        if (proxy.Length == 0)
        {
            throw new Exception("Pixiv 搜图要走 pixiv.net，本机实测国内直连不通，得在插件配置「搜图·代理地址」里填一个HTTP代理（如 http://127.0.0.1:7890）；用户只给了作品ID或链接的话，直接 PixivSend 就不需要代理");
        }
        string encoded = Uri.EscapeDataString(keyword.Trim());
        string url = SearchBase + encoded + "?word=" + encoded
            + "&order=" + (IsNewest(order) ? "descend_date" : "popular")
            + "&sort_order=desc&p=1&target=all&search_target=illust";
        JsonElement body = await GetJsonAsync(url, proxy, TimeoutSeconds(false));
        if (body.TryGetProperty("illustManga", out JsonElement illustManga) == false
            || illustManga.TryGetProperty("data", out JsonElement data) == false
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new Exception("Pixiv 搜索返回的形状和实测不一样，没找到 body.illustManga.data");
        }
        return ParseHits(data, limit);
    }

    // ajax/illust/{id} 免登录可用（实测），只用来给按ID发送补标题与页数；搜索接口已有这些字段，不必再请
    async Task<PixivHit?> FetchHitAsync(string id)
    {
        try
        {
            JsonElement body = await GetJsonAsync(IllustBase + id, Configuration.ProxyUrl, TimeoutSeconds(false));
            string title = GetText(body, "illustTitle");
            if (title.Length == 0)
            {
                return null;
            }
            return new PixivHit(id, title, GetText(body, "userName"), GetInt(body, "width"), GetInt(body, "height"),
                GetInt(body, "pageCount"), "", GetInt(body, "illustType") != 0);
        }
        catch (Exception detailError)
        {
            logger.LogInformation("[Pixiv] 取作品 {Id} 详情失败，只按ID发送：{Error}", id, detailError.Message);
            return null;
        }
    }

    static List<PixivHit> ParseHits(JsonElement data, int limit)
    {
        List<PixivHit> hits = new();
        foreach (JsonElement item in data.EnumerateArray())
        {
            // 结果数组里会混进 {"isAdContainer":true} 这种广告位，它没有 id 与 url
            string id = GetText(item, "id");
            if (id.Length == 0 || GetInt(item, "xRestrict") != 0)
            {
                continue;
            }
            hits.Add(new PixivHit(id, GetText(item, "title"), GetText(item, "userName"),
                GetInt(item, "width"), GetInt(item, "height"), GetInt(item, "pageCount"),
                JoinTags(item), GetInt(item, "illustType") != 0));
            if (hits.Count >= limit)
            {
                break;
            }
        }
        return hits;
    }

    // 搜索接口里 tags 是字符串数组（实测）
    static string JoinTags(JsonElement item)
    {
        if (item.TryGetProperty("tags", out JsonElement tags) == false || tags.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        List<string> names = new();
        foreach (JsonElement tag in tags.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.String)
            {
                names.Add(tag.GetString() ?? "");
            }
            if (names.Count >= 3)
            {
                break;
            }
        }
        return string.Join("、", names);
    }

    static bool IsNewest(string? order) => string.Equals(order, "new", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    // 下载
    // ============================================================

    sealed record PixivFile(string Path, bool FromCache);

    async Task<PixivFile> DownloadAsync(string id, int page)
    {
        string folder = Path.Combine(AlifePath.StorageFolderPath, Configuration.ImageFolder.Trim());
        Directory.CreateDirectory(folder);
        foreach (string cached in Directory.EnumerateFiles(folder, id + "-" + page + ".*"))
        {
            if (new FileInfo(cached).Length > 0)
            {
                return new PixivFile(cached, true);
            }
        }

        // 镜像扩展名只是占位：实测 .png/.gif 都回同一份 JPEG，真类型只能看 Content-Type。
        // 第1页必须用 {id}.jpg —— {id}-1.jpg 这种写法实测对部分作品直接404，第2页起才带页码
        string url = MirrorBase + id + (page > 1 ? "-" + page : "") + ".jpg";
        using HttpClient client = NewHttpClient("", TimeoutSeconds(true));
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, DestroyCancellationToken);
        if (response.IsSuccessStatusCode == false)
        {
            throw new Exception("pixiv.re 回 " + (int)response.StatusCode + "：" + MirrorHint((int)response.StatusCode, id, page));
        }
        string extension = ExtensionOf(response.Content.Headers.ContentType?.MediaType ?? "");
        // 先读满再一次性落盘，超时或半途失败时不会留下半个文件被下次当成缓存
        byte[] image = await response.Content.ReadAsByteArrayAsync(DestroyCancellationToken);
        if (image.Length == 0)
        {
            throw new Exception("pixiv.re 回的是空文件");
        }
        string path = Path.Combine(folder, id + "-" + page + extension);
        await File.WriteAllBytesAsync(path, image, DestroyCancellationToken);
        logger.LogInformation("[Pixiv] 已下载 {Id} 第{Page}页 {Bytes} 字节", id, page, image.Length);
        return new PixivFile(path, false);
    }

    static string ExtensionOf(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => throw new Exception("pixiv.re 回的不是可识别的图片（Content-Type " + (mediaType.Length == 0 ? "空" : mediaType) + "）")
    };

    static string MirrorHint(int statusCode, string id, int page) => statusCode switch
    {
        404 => "作品 " + id + " 不存在，或它没有第 " + page + " 页（多P作品的页码从1开始）",
        400 => "镜像不认这个URL形式（作品ID得是纯数字）",
        _ => "镜像这条URL取不到图"
    };

    // ============================================================
    // 发送
    // ============================================================

    async Task SendAndReportAsync(PixivHit? hit, string id, int page, PixivFile file, long target, bool isGroup)
    {
        bool success;
        string failure = "";
        try
        {
            (success, failure) = await SendSegmentAsync(target, isGroup, file.Path);
        }
        catch (Exception sendError)
        {
            success = false;
            failure = sendError.Message;
        }
        if (success == false)
        {
            interactor.Poke("❌ 图已下载到本地但没发出去：" + failure + "\n文件：" + file.Path);
            return;
        }
        long bytes = new FileInfo(file.Path).Length;
        List<string> report = new()
        {
            "✅ 图片已发送：" + (hit == null ? "pixiv " + id : hit.Title + "／" + hit.UserName),
            "　" + id + " 第" + page + "页，" + SizeText(bytes) + (file.FromCache ? "，本地缓存，没重新下载" : "")
                + "，https://www.pixiv.net/artworks/" + id
        };
        if (hit != null && hit.PageCount > page)
        {
            report.Add("　这条作品共 " + hit.PageCount + " 页，要后面的页再发一次带 page。");
        }
        interactor.Poke(string.Join("\n", report));
    }

    async Task<(bool Success, string Message)> SendSegmentAsync(long target, bool isGroup, string filePath)
    {
        await ResolveProviderAsync();
        JsonArray segments = new();
        segments.Add(new JsonObject
        {
            ["type"] = "image",
            ["data"] = new JsonObject { ["file"] = filePath }
        });
        string action = isGroup ? "send_group_msg" : "send_private_msg";
        JsonObject parameters = new()
        {
            [isGroup ? "group_id" : "user_id"] = target,
            ["message"] = segments
        };
        (int retcode, string message, JsonElement data) = await SendActionAsync(action, parameters);
        if (retcode != 0)
        {
            return (false, ProviderLabel() + " 返回 " + retcode + "：" + message + FailureHint(retcode));
        }
        // 三端成功都带 message_id；LLOneBot 对不认识的段不报错直接丢，所以只认 retcode 不够
        if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("message_id", out _) == false)
        {
            return (false, ProviderLabel() + " 回成功但没给 message_id，image 段可能被静默丢弃");
        }
        return (true, "");
    }

    string FailureHint(int retcode) => retcode switch
    {
        1404 => "（" + ProviderLabel() + " 未实现该接口）",
        1400 or 1200 => "（" + ProviderLabel() + " 不认识这个消息段，整条已退回；image 是 OneBot 标准段，多半是本地文件路径读不到）",
        _ => ""
    };

    string ProviderLabel() => providerName.Length > 0 ? providerName : "协议端";

    async Task ResolveProviderAsync()
    {
        if (providerResolved)
        {
            return;
        }
        providerResolved = true;
        (int retcode, string message, JsonElement data) = await SendActionAsync("get_version_info", new JsonObject());
        if (retcode != 0)
        {
            throw new Exception("get_version_info 返回 " + retcode + "：" + message);
        }
        providerName = (GetText(data, "app_name") + " " + GetText(data, "name") + GetText(data, "vendor")).Trim();
        logger.LogInformation("[Pixiv] 协议端：{Provider} {Version}", providerName, GetText(data, "app_version"));
    }

    async Task<(int Retcode, string Message, JsonElement Data)> SendActionAsync(string action, JsonObject parameters)
    {
        await wsLock.WaitAsync();
        try
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await SendActionOnceAsync(action, parameters);
                }
                catch (Exception attemptError)
                {
                    lastError = attemptError;
                    sharedWs?.Dispose();
                    sharedWs = null;
                }
            }
            throw new Exception(action + " 失败：" + lastError!.Message);
        }
        finally
        {
            wsLock.Release();
        }
    }

    async Task<(int Retcode, string Message, JsonElement Data)> SendActionOnceAsync(string action, JsonObject parameters)
    {
        if (sharedWs == null || sharedWs.State != WebSocketState.Open)
        {
            sharedWs?.Dispose();
            ClientWebSocket connection = new();
            using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(10));
            await connection.ConnectAsync(new Uri(Configuration.WsUrl), connectTimeout.Token);
            sharedWs = connection;
        }
        string echo = Guid.NewGuid().ToString();
        JsonObject payload = new()
        {
            ["action"] = action,
            ["params"] = parameters,
            ["echo"] = echo
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        byte[] receiveBuffer = new byte[64 * 1024];
        await sharedWs.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload.ToJsonString())),
            WebSocketMessageType.Text, true, timeout.Token);
        while (timeout.IsCancellationRequested == false)
        {
            MemoryStream buffer = new();
            WebSocketReceiveResult received;
            do
            {
                received = await sharedWs.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new Exception("协议端关闭了连接");
                }
                buffer.Write(receiveBuffer, 0, received.Count);
            } while (received.EndOfMessage == false);

            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            // 事件推送与别的响应都会路过，只认自己 echo 的那一条
            if (document.RootElement.TryGetProperty("echo", out JsonElement echoElement) == false
                || echoElement.GetString() != echo)
            {
                continue;
            }
            int retcode = document.RootElement.TryGetProperty("retcode", out JsonElement retcodeElement)
                && retcodeElement.ValueKind == JsonValueKind.Number ? retcodeElement.GetInt32() : -1;
            JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement dataElement)
                ? dataElement.Clone() : JsonDocument.Parse("null").RootElement.Clone();
            return (retcode, GetText(document.RootElement, "message"), data);
        }
        throw new Exception("等协议端响应超时");
    }

    // ============================================================
    // 杂项
    // ============================================================

    static readonly Regex artworkIdPattern = new(@"pixiv\.net/(?:[a-z]{2}/)?(?:artworks|illust)/(\d+)", RegexOptions.IgnoreCase);

    // 认作品ID与作品链接，认不出来就当成关键词
    static string ExtractArtworkId(string source)
    {
        string text = source.Trim();
        Match match = artworkIdPattern.Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        return Regex.IsMatch(text, @"^\d+$") ? text : "";
    }

    static HttpClient NewHttpClient(string proxy, int timeoutSeconds)
    {
        HttpClientHandler handler = new();
        if (proxy.Trim().Length > 0)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy.Trim());
        }
        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    async Task<JsonElement> GetJsonAsync(string url, string proxy, int timeoutSeconds)
    {
        using HttpClient client = NewHttpClient(proxy, timeoutSeconds);
        using HttpResponseMessage response = await client.GetAsync(url, DestroyCancellationToken);
        if (response.IsSuccessStatusCode == false)
        {
            throw new Exception("Pixiv 接口回 HTTP " + (int)response.StatusCode);
        }
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(DestroyCancellationToken));
        JsonElement root = document.RootElement.Clone();
        if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.True)
        {
            throw new Exception("Pixiv 接口报错：" + Shorten(GetText(root, "message")));
        }
        if (root.TryGetProperty("body", out JsonElement body) == false)
        {
            throw new Exception("Pixiv 接口没回 body");
        }
        return body;
    }

    int TimeoutSeconds(bool isDownload)
    {
        int raw = isDownload ? Configuration.DownloadTimeoutSeconds : Configuration.SearchTimeoutSeconds;
        return Math.Clamp(raw, 5, 600);
    }

    async Task<string> ProbeAsync(string url, string proxy)
    {
        using HttpClient client = NewHttpClient(proxy, ProbeTimeoutSeconds);
        try
        {
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, DestroyCancellationToken);
            return (int)response.StatusCode + "（通）";
        }
        catch (Exception probeError)
        {
            return "不通（" + probeError.GetType().Name + "）";
        }
    }

    string DescribeFolder()
    {
        string folder = Path.Combine(AlifePath.StorageFolderPath, Configuration.ImageFolder.Trim());
        if (Directory.Exists(folder) == false)
        {
            return folder + "（还没建，第一次取图时自动创建）";
        }
        long bytes = 0;
        int count = 0;
        foreach (string file in Directory.GetFiles(folder))
        {
            bytes += new FileInfo(file).Length;
            count++;
        }
        return folder + "（" + count + " 个文件，" + SizeText(bytes) + "）";
    }

    static string SizeText(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
        return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
    }

    static string Shorten(string text)
    {
        string oneLine = Regex.Replace(text, "\\s+", " ").Trim();
        return oneLine.Length > 300 ? oneLine[..300] + "…" : oneLine;
    }

    static string GetText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty(name, out JsonElement value) == false)
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    static int GetInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    }

    bool Gate(bool enabled, string name)
    {
        if (enabled)
        {
            return false;
        }
        interactor.Poke(name + "功能已在插件配置中关闭");
        return true;
    }
}
