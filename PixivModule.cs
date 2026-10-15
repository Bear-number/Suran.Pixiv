using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
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

    [DisplayName("搜图·排除标签")]
    [Description("命中这些标签的作品不搜也不发，多个用逗号或顿号分隔。判定是包含式：填 AI 也能挡掉 AIイラスト、AI生成")]
    public string ExcludeTags { get; set; } = "AI,R-18,R-18G,裸足";

    [DisplayName("搜图·最低收藏数")]
    [Description("0=不筛。填上之后候选会走作品详情读真实收藏数（实测收藏数只在详情接口里给），并按步长取最多12条样逐条请，一次搜索因此要多花十几秒。免登录只搜得到最新上传的几页，收藏数普遍个位到几十，门槛设上百基本全军覆没")]
    public int MinBookmarks { get; set; } = 0;

    [DisplayName("搜图·关键词映射")]
    [Description("中文关键词转成Pixiv上真正在用的tag，格式 中文=日文，多条用分号分隔。留空只用内置表，填了则在内置表基础上覆盖")]
    public string KeywordMap { get; set; } = "";

    [DisplayName("发送·合并转发")]
    [Description("开启后一次发多张图合成一条合并转发消息，不刷屏；失败自动退回逐条发送。三端都注册了 send_forward_msg 动作，但未实机验证")]
    public bool SendAsForward { get; set; } = false;

    [DisplayName("去重·有效小时")]
    [Description("发过的作品在这个时限内不会再出现在搜索结果里，默认24小时；0=发过的永久不再出现")]
    public int DedupExpireHours { get; set; } = 24;

    [DisplayName("缓存·文件上限")]
    [Description("图片目录里的图片超过这个数量时，先删最旧的，默认50")]
    public int MaxCacheFiles { get; set; } = 50;

    [DisplayName("缓存·每次删几个")]
    [Description("超过上限时一次删掉最旧的几个，默认20")]
    public int CleanupCount { get; set; } = 20;

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
    [Description("关闭后 PixivSend / PixivSendMulti 不可用，也不会下载图片；PixivSearch 只查不发，不受影响")]
    public bool EnableSend { get; set; } = true;
}

[Module("P站图片搜索发送",
    "免登录搜 Pixiv 作品并把原图以QQ图片消息发出：关键词搜图取候选、按作品ID或链接直接发、一次批量发多张（可合并转发）、取同画师的其他作品；带排除标签、收藏数门槛、中文关键词转tag、发过的自动去重、本地缓存自动清理",
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
    const string ArtworkPageBase = "https://www.pixiv.net/artworks/";
    const string TestKeyword = "初音ミク";
    const string LedgerName = "sent_records.json";
    const int MaxResults = 20;
    const int ProbeTimeoutSeconds = 8;
    const int BookmarksProbeLimit = 12;
    const int MaxBatchSend = 6;
    // 免登录没有人气排序，只能把最新上传的前几页当候选池本地排（实测 p=1..3 各回约59条且不重叠）
    const int PopularSearchPages = 3;
    const int PopularityPoolSize = 120;

    // 中文口头说法 → Pixiv 上实际在用的 tag（照着 KiraAI 那份插件的表搬的，那些词是P站搜得到图的词）
    static readonly Dictionary<string, string> BuiltInKeywordMap = new()
    {
        ["黑丝"] = "黒タイツ", ["白丝"] = "白タイツ", ["丝袜"] = "タイツ", ["腿"] = "太もも", ["大腿"] = "太もも",
        ["脚"] = "足", ["裸足"] = "裸足", ["萝莉"] = "ロリ", ["少女"] = "少女", ["御姐"] = "お姉さん",
        ["泳装"] = "水着", ["制服"] = "制服", ["女仆"] = "メイド", ["兔女郎"] = "バニーガール", ["和服"] = "着物",
        ["婚纱"] = "ウェディングドレス", ["猫耳"] = "猫耳", ["狐耳"] = "狐耳", ["原神"] = "原神", ["崩坏"] = "崩壊",
        ["崩铁"] = "崩壊:スターレイル", ["星穹铁道"] = "崩壊:スターレイル", ["碧蓝航线"] = "アズールレーン",
        ["明日方舟"] = "アークナイツ", ["FGO"] = "Fate/GrandOrder", ["初音"] = "初音ミク"
    };

    static ClientWebSocket? sharedWs;
    static readonly SemaphoreSlim wsLock = new(1, 1);
    static string providerName = "";
    static bool providerResolved;
    static string selfId = "";
    static string selfNick = "";

    readonly Dictionary<string, long> sentLedger = new();
    readonly object ledgerLock = new();
    bool ledgerLoaded;

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "Pixiv 搜图并发图片：搜索作品候选、按关键词或作品ID把原图发到QQ群/私聊、一次发多张、取同画师的其他作品。",
            Explanation = "全程不用登录 Pixiv 账号。搜索走 pixiv.net 的公开接口，国内直连不通，需要在插件配置里填「搜图·代理地址」；"
                + "图片本身走 pixiv.re 镜像，国内直连就能取，给的是原图（P站的预览图地址实测拿不到，别再提原图/预览的切换）。"
                + "免登录接口实测只回最新上传（order、s_mode 这些排序参数一律被忽略，人气榜与排行榜都要登录），所以人气是插件自己兼容出来的："
                + "多翻几页取候选、按步长抽样去详情接口读真实收藏数再排；要快就传 order=new 只要最新上传。排不到全站经典热图，用户要那种就让他给作品ID或链接。"
                + "用户说『来张XX的图』：只发一张就 PixivSearch 看候选再 PixivSend，要一次好几张直接 PixivSendMulti。"
                + "中文关键词会自动按映射表转成P站的tag（黑丝→黒タイツ 之类），配置里还能自己加。"
                + "搜出来的候选已经自动排掉命中排除标签的、收藏数不够的和最近发过的；发过之后24小时内不会再被搜到，避免同一张反复刷。"
                + "用户说『这个画师的其他图』『XX画师的图』：拿任一条作品ID调 PixivArtist。"
                + "用户直接给了作品ID或作品链接时不用搜索，也就不用代理。取不到图或发不出去先跑 PixivEnv。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜Pixiv作品候选（免登录，需要配置代理）。返回作品ID、标题、作者、尺寸、页数、标签，供挑定后用 PixivSend 发送。order 留空或 popular=按收藏数从多到少（免登录没有服务端人气榜，插件多翻几页取样、逐条读真实收藏数在本地排），order=new=只要最新上传、快。命中排除标签、收藏数不到门槛、以及最近发过的都不会出现在这里")]
    public async Task PixivSearch(
        [Description("关键词，日文tag最准（如 初音ミク），中文和角色名也能搜，中文会先过一遍映射表")] string keyword,
        [Description("返回几条，1-20，默认8")] int count = 8,
        [Description("留空或 popular=收藏多的优先（免登录没有人气榜，插件多翻几页取样、本地按真实收藏数排，慢）/ new=最新上传（快）")] string? order = null)
    {
        if (keyword.Trim().Length == 0) throw new Exception("没给关键词");
        string mapped = MapKeyword(keyword);
        int limit = Math.Clamp(count, 1, MaxResults);
        List<PixivHit> hits = await PickAsync(mapped, limit, order);
        List<string> lines = new()
        {
            "Pixiv 搜索「" + keyword.Trim() + "」" + (mapped == keyword.Trim() ? "" : "（按映射表转成「" + mapped + "」）")
                + "（" + (IsNewest(order) && Configuration.MinBookmarks <= 0 ? "最新上传" : "按收藏数排") + "，" + hits.Count + " 条）："
        };
        if (hits.Count == 0)
        {
            lines.Add("没有可发的结果。未登录接口本身就不回 R-18 与私有作品，此外已排掉命中排除标签（"
                + ExcludesText() + "）和最近 " + DedupWindowText() + "发过的；关键词换个说法或改日文tag再试。");
            interactor.Poke(string.Join("\n", lines));
            return;
        }
        foreach (PixivHit hit in hits)
        {
            lines.Add("- " + Describe(hit));
        }
        lines.Add("发图用 PixivSend(作品ID)，多P作品带 page（从1开始）；一次要好几张用 PixivSendMulti；图片走 pixiv.re 镜像取原图，不需要代理。");
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从Pixiv取一张原图并以QQ图片消息发出（不是文件消息）。source可以是作品ID、作品链接，也可以是搜索关键词（关键词就发筛过的第一条，走关键词需要配置代理）；page是多页作品的第几页")]
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
            List<PixivHit> hits = await PickAsync(MapKeyword(source), 1, null);
            if (hits.Count == 0)
            {
                interactor.Poke("Pixiv 搜「" + source + "」没有可发的作品（排除标签、收藏数门槛、发过去的都会挡掉），换个关键词或用 PixivSearch 看候选。");
                return;
            }
            hit = hits[0];
            id = hit.Id;
        }
        else if (Configuration.ProxyUrl.Trim().Length > 0)
        {
            hit = await FetchHitAsync(id);
        }

        CleanupCache();
        PixivFile file = await DownloadAsync(id, page);
        await SendAndReportAsync(hit, id, page, file, targetId.Value, isGroup);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词一次发多张Pixiv原图（用户说『来几张XX』时用）。候选同样过排除标签、收藏数门槛和发过去的去重，取到几张发几张；配置开了合并转发就一条消息发出，没开或转发失败就逐条发")]
    public async Task PixivSendMulti(
        [Description("搜索关键词，中文会自动过映射表")] string keyword,
        [Description("目标群号或QQ号")] long? targetId = null,
        [Description("发几张，1-6，默认3")] int count = 3,
        [Description("group或private，默认group")] string? type = null,
        [Description("留空或 popular=收藏多的优先（慢）/ new=最新上传（快）")] string? order = null)
    {
        if (Gate(Configuration.EnableSend, "发送图片")) return;
        if (targetId == null) throw new Exception("没给目标，发群里还是私聊得说一个");
        if (keyword.Trim().Length == 0) throw new Exception("没给关键词");
        bool isGroup = !string.Equals(type, "private", StringComparison.OrdinalIgnoreCase);
        int want = Math.Clamp(count, 1, MaxBatchSend);

        string mapped = MapKeyword(keyword);
        List<PixivHit> hits = await PickAsync(mapped, want, order);
        if (hits.Count == 0)
        {
            interactor.Poke("Pixiv 搜「" + keyword.Trim() + "」没有可发的作品（排除标签 " + ExcludesText()
                + "、收藏数门槛 " + Configuration.MinBookmarks + "、最近 " + DedupWindowText() + " 发过的都已排掉）。");
            return;
        }

        CleanupCache();
        List<(PixivHit Hit, PixivFile File)> ready = new();
        List<string> skipped = new();
        foreach (PixivHit hit in hits)
        {
            try
            {
                ready.Add((hit, await DownloadAsync(hit.Id, 1)));
            }
            catch (Exception downloadError)
            {
                skipped.Add(hit.Id + "：" + downloadError.Message);
            }
        }
        if (ready.Count == 0)
        {
            interactor.Poke("❌ 一条图都没取到：" + string.Join("；", skipped));
            return;
        }

        List<string> sentIds = new();
        List<string> failures = new();
        if (Configuration.SendAsForward && ready.Count > 1)
        {
            (bool forwarded, string forwardError) = await SendForwardAsync(targetId.Value, isGroup,
                ready.Select(item => item.File.Path).ToList());
            if (forwarded)
            {
                sentIds = ready.Select(item => item.Hit.Id).ToList();
            }
            else
            {
                failures.Add("合并转发没成（" + forwardError + "），改逐条发");
            }
        }
        if (Configuration.SendAsForward == false || sentIds.Count == 0)
        {
            foreach ((PixivHit hit, PixivFile file) in ready)
            {
                (bool success, string message) = await SendSegmentAsync(targetId.Value, isGroup, file.Path);
                if (success)
                {
                    sentIds.Add(hit.Id);
                }
                else
                {
                    failures.Add(hit.Id + "：" + message);
                }
            }
        }
        foreach (string id in sentIds)
        {
            MarkSent(id);
        }

        List<string> report = new()
        {
            "✅ Pixiv「" + keyword.Trim() + "」发出 " + sentIds.Count + " 张"
                + (Configuration.SendAsForward ? "（合并转发）" : "（逐条）") + "："
                + string.Join("、", sentIds.Select(id => ArtworkPageBase + id))
        };
        foreach ((PixivHit hit, PixivFile file) in ready)
        {
            if (sentIds.Contains(hit.Id))
            {
                report.Add("　" + hit.Title + "／" + hit.UserName);
            }
        }
        if (skipped.Count > 0) report.Add("没取到图：" + string.Join("；", skipped));
        if (failures.Count > 0) report.Add("没发出去：" + string.Join("；", failures));
        report.Add("这几张在 " + DedupWindowText() + " 内不会再被搜到。图片由工具直接发完了，不用再自己发一遍。");
        interactor.Poke(string.Join("\n", report));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("取同一个画师的其他作品ID（用户说『这个画师的其他图』『再来几张XX画师的』时用）。source给作品ID或作品链接最准；只给画师名或关键词时，先按关键词搜一条出来再取它画师的作品。免登录接口不给按画师直接搜，只能这样顺着作品带出来")]
    public async Task PixivArtist(
        [Description("作品ID、作品链接，或者画师名/关键词")] string source,
        [Description("返回几条，1-20，默认10")] int count = 10)
    {
        if (source.Trim().Length == 0) throw new Exception("没给作品ID或关键词");
        string seedId = ExtractArtworkId(source);
        string fromNote = "";
        if (seedId.Length == 0)
        {
            List<PixivHit> hits = await PickAsync(MapKeyword(source), 1, null);
            if (hits.Count == 0)
            {
                interactor.Poke("没能从「" + source + "」定位到一条作品。免登录取同画师作品得先有一条作品ID或链接，让用户给一条，或者先用 PixivSearch 搜出来。");
                return;
            }
            seedId = hits[0].Id;
            fromNote = "（按关键词「" + source.Trim() + "」搜到的第一条作品带出来的画师）";
        }

        JsonElement detail = await GetJsonAsync(IllustBase + seedId, RequireProxy(), TimeoutSeconds(false));
        string artist = GetText(detail, "userName");
        List<string> ids = new();
        if (detail.TryGetProperty("userIllusts", out JsonElement userIllusts) && userIllusts.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in userIllusts.EnumerateObject())
            {
                if (entry.Name != seedId && IsRecentlySent(entry.Name) == false)
                {
                    ids.Add(entry.Name);
                }
            }
        }
        ids = ids.OrderByDescending(id => id.Length).ThenByDescending(id => id).Take(Math.Clamp(count, 1, MaxResults)).ToList();
        if (ids.Count == 0)
        {
            interactor.Poke("作品 " + seedId + " 的画师 " + artist + " 这边列不出别的作品（免登录接口没给同作者列表，或只剩这一条）。");
            return;
        }
        List<string> lines = new()
        {
            "画师 " + artist + " 的其他作品 " + ids.Count + " 条 " + fromNote + "（按ID从新到旧，未登录列表给不出标题和标签，得逐条打开才知道）："
        };
        lines.AddRange(ids.Select(id => "- " + id + " " + ArtworkPageBase + id));
        lines.Add("发图用 PixivSend(作品ID)；这条作品是 " + seedId + "，共 " + GetInt(detail, "pageCount") + " 页。");
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("自检：pixiv.net 直连与经代理通不通、搜索接口能不能用、pixiv.re 镜像通不通、图片目录与缓存清理状况、去重记录、筛选配置、协议端是哪个端")]
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
                List<PixivHit> hits = await PickAsync(TestKeyword, 3, null);
                lines.Add("搜索接口：可用，试搜「" + TestKeyword + "」拿到 " + hits.Count + " 条，第一条 "
                    + (hits.Count > 0 ? Describe(hits[0]) : "（被筛干净了）")
                    + "；免登录接口只回最新上传（实测排序参数被忽略），人气是本地多翻几页按收藏数排的");
            }
            catch (Exception searchError)
            {
                lines.Add("❌ 搜索接口：" + searchError.Message);
            }
        }
        lines.Add("pixiv.re 镜像（直连取原图）：" + await ProbeAsync(MirrorBase, ""));
        lines.Add("筛选：排除标签 " + ExcludesText()
            + "，收藏数门槛 " + (Configuration.MinBookmarks > 0 ? Configuration.MinBookmarks.ToString(CultureInfo.InvariantCulture) : "不设")
            + "，关键词映射 " + KeywordMapTable().Count + " 条");
        lines.Add("去重：最近 " + DedupWindowText() + " 发过的不再搜到，当前记录 " + LedgerCount() + " 条");
        lines.Add("图片目录：" + DescribeFolder());
        try
        {
            await ResolveProviderAsync();
            lines.Add("协议端：" + ProviderLabel() + "，地址 " + Configuration.WsUrl
                + "，机器人 " + (selfNick.Length > 0 ? selfNick : "?") + "(" + selfId + ")");
        }
        catch (Exception wsError)
        {
            lines.Add("❌ 协议端连不上（" + Configuration.WsUrl + "）：" + wsError.Message);
        }
        lines.Add("开关：发送" + (Configuration.EnableSend ? "开" : "关")
            + "，合并转发" + (Configuration.SendAsForward ? "开" : "关")
            + "，超时 搜索" + TimeoutSeconds(false) + "秒/取图" + TimeoutSeconds(true) + "秒");
        interactor.Poke(string.Join("\n", lines));
    }

    // ============================================================
    // 搜索
    // ============================================================

    sealed record PixivHit(string Id, string Title, string UserName, int Width, int Height, int PageCount,
        string[] Tags, bool IsAnimation, int Bookmarks);

    // 免登录没有服务端人气榜（实测 order/s_mode 一律被忽略、排行接口404），人气只能在本地排：
    // 多翻几页把候选池撑大，按步长取样逐条读真实收藏数再排序；order=new 就跳过这一趟直接回最新上传
    async Task<List<PixivHit>> PickAsync(string keyword, int limit, string? order)
    {
        if (IsNewest(order) && Configuration.MinBookmarks <= 0)
        {
            return await SearchAsync(keyword, limit, 1);
        }
        List<PixivHit> pool = await SearchAsync(keyword, PopularityPoolSize, PopularSearchPages);
        List<PixivHit> kept = new();
        foreach (PixivHit hit in Spread(pool, BookmarksProbeLimit))
        {
            PixivHit? detail = await FetchHitAsync(hit.Id);
            PixivHit checkedHit = detail == null ? hit : detail with { Title = hit.Title, UserName = hit.UserName };
            if (Excluded(checkedHit.Tags) == false && checkedHit.Bookmarks >= Configuration.MinBookmarks)
            {
                kept.Add(checkedHit);
            }
        }
        return kept.OrderByDescending(hit => hit.Bookmarks).Take(limit).ToList();
    }

    // 只取池子头几条等于在一小时内的新图里排，收藏数全是个位数排了跟没排一样；按步长取样才排得出相对人气
    static List<PixivHit> Spread(List<PixivHit> pool, int want)
    {
        if (pool.Count <= want)
        {
            return pool;
        }
        int stride = (pool.Count + want - 1) / want;
        List<PixivHit> picked = new();
        for (int index = 0; index < pool.Count && picked.Count < want; index += stride)
        {
            picked.Add(pool[index]);
        }
        return picked;
    }

    async Task<List<PixivHit>> SearchAsync(string keyword, int limit, int pages)
    {
        string proxy = RequireProxy();
        string encoded = Uri.EscapeDataString(keyword.Trim());
        List<PixivHit> hits = new();
        for (int page = 1; page <= pages && hits.Count < limit; page++)
        {
            string url = SearchBase + encoded + "?word=" + encoded
                + "&order=descend_date&sort_order=desc&p=" + page + "&target=all&search_target=illust";
            JsonElement body = await GetJsonAsync(url, proxy, TimeoutSeconds(false));
            if (body.TryGetProperty("illustManga", out JsonElement illustManga) == false
                || illustManga.TryGetProperty("data", out JsonElement data) == false
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new Exception("Pixiv 搜索返回的形状和实测不一样，没找到 body.illustManga.data");
            }
            hits.AddRange(ParseHits(data, limit - hits.Count));
        }
        return hits;
    }

    static bool IsNewest(string? order) => string.Equals(order, "new", StringComparison.OrdinalIgnoreCase);

    // ajax/illust/{id} 免登录可用（实测给 title/userName/宽高/页数/收藏数/标签），搜索接口没给收藏数，只有这里能拿到
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
                GetInt(body, "pageCount"), DetailTags(body), GetInt(body, "illustType") != 0, GetInt(body, "bookmarkCount"));
        }
        catch (Exception detailError)
        {
            logger.LogInformation("[Pixiv] 取作品 {Id} 详情失败，只按ID发送：{Error}", id, detailError.Message);
            return null;
        }
    }

    List<PixivHit> ParseHits(JsonElement data, int limit)
    {
        List<string> excludes = ExcludeList();
        List<PixivHit> hits = new();
        foreach (JsonElement item in data.EnumerateArray())
        {
            // 结果数组里会混进 {"isAdContainer":true} 这种广告位，它没有 id 与 url
            string id = GetText(item, "id");
            string[] tags = SearchTags(item);
            if (id.Length == 0 || GetInt(item, "xRestrict") != 0 || Excluded(tags, excludes) || IsRecentlySent(id))
            {
                continue;
            }
            hits.Add(new PixivHit(id, GetText(item, "title"), GetText(item, "userName"),
                GetInt(item, "width"), GetInt(item, "height"), GetInt(item, "pageCount"),
                tags, GetInt(item, "illustType") != 0, 0));
            if (hits.Count >= limit)
            {
                break;
            }
        }
        return hits;
    }

    // 搜索接口里 tags 是字符串数组（实测）
    static string[] SearchTags(JsonElement item)
    {
        if (item.TryGetProperty("tags", out JsonElement tags) == false || tags.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return tags.EnumerateArray()
            .Where(tag => tag.ValueKind == JsonValueKind.String)
            .Select(tag => tag.GetString() ?? "")
            .ToArray();
    }

    // 详情接口的 tags 是 {tags:[{tag:xx}]} 套一层（实测）
    static string[] DetailTags(JsonElement body)
    {
        if (body.TryGetProperty("tags", out JsonElement wrapper) == false
            || wrapper.TryGetProperty("tags", out JsonElement tags) == false
            || tags.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return tags.EnumerateArray()
            .Where(tag => tag.ValueKind == JsonValueKind.Object)
            .Select(tag => GetText(tag, "tag"))
            .Where(name => name.Length > 0)
            .ToArray();
    }

    string Describe(PixivHit hit)
    {
        return hit.Id + " " + hit.Title + "／" + hit.UserName + "  " + hit.Width + "x" + hit.Height
            + (hit.PageCount > 1 ? " " + hit.PageCount + "P" : "")
            + (hit.IsAnimation ? " 动图(只能取静帧)" : "")
            + (hit.Bookmarks > 0 ? " 收藏" + hit.Bookmarks : "")
            + (hit.Tags.Length > 0 ? "  标签: " + string.Join("、", hit.Tags.Take(3)) : "");
    }

    // ============================================================
    // 关键词映射与标签筛选
    // ============================================================

    Dictionary<string, string> KeywordMapTable()
    {
        Dictionary<string, string> table = new(BuiltInKeywordMap);
        foreach (string pair in SplitList(Configuration.KeywordMap, ';'))
        {
            int split = pair.IndexOf('=');
            if (split > 0)
            {
                table[pair[..split].Trim()] = pair[(split + 1)..].Trim();
            }
        }
        return table;
    }

    // 整个词命中映射表就整词替换，否则按空格切开逐个替换（照着 KiraAI 那份的做法）
    string MapKeyword(string keyword)
    {
        string trimmed = keyword.Trim();
        Dictionary<string, string> table = KeywordMapTable();
        if (table.TryGetValue(trimmed, out string? whole))
        {
            return whole;
        }
        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return trimmed;
        }
        return string.Join(" ", parts.Select(part => table.TryGetValue(part, out string? mapped) ? mapped : part));
    }

    List<string> ExcludeList() => SplitList(Configuration.ExcludeTags, ',');

    bool Excluded(string[] tags) => Excluded(tags, ExcludeList());

    static bool Excluded(string[] tags, List<string> excludes)
    {
        foreach (string tag in tags)
        {
            foreach (string exclude in excludes)
            {
                if (tag.Contains(exclude, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    string ExcludesText()
    {
        List<string> excludes = ExcludeList();
        return excludes.Count == 0 ? "（没设）" : string.Join("、", excludes);
    }

    static List<string> SplitList(string text, char extraSeparator)
    {
        return text
            .Split(new[] { ',', '，', '、', ';', '；', extraSeparator }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToList();
    }

    // ============================================================
    // 发过的去重
    // ============================================================

    string LedgerPath => Path.Combine(ImageFolderPath(), LedgerName);

    void EnsureLedgerLoaded()
    {
        lock (ledgerLock)
        {
            if (ledgerLoaded)
            {
                return;
            }
            ledgerLoaded = true;
            if (File.Exists(LedgerPath) == false)
            {
                return;
            }
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(LedgerPath));
                foreach (JsonProperty entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Number)
                    {
                        sentLedger[entry.Name] = entry.Value.GetInt64();
                    }
                }
            }
            catch (Exception loadError)
            {
                logger.LogWarning("[Pixiv] 去重记录读不出来，重新开始：{Error}", loadError.Message);
                sentLedger.Clear();
            }
            PruneLedger();
            logger.LogInformation("[Pixiv] 去重记录 {Count} 条", sentLedger.Count);
        }
    }

    bool IsRecentlySent(string id)
    {
        EnsureLedgerLoaded();
        lock (ledgerLock)
        {
            if (sentLedger.TryGetValue(id, out long sentAt) == false)
            {
                return false;
            }
            int hours = Configuration.DedupExpireHours;
            if (hours > 0 && UnixNow() - sentAt > hours * 3600L)
            {
                sentLedger.Remove(id);
                SaveLedger();
                return false;
            }
            return true;
        }
    }

    void MarkSent(string id)
    {
        EnsureLedgerLoaded();
        lock (ledgerLock)
        {
            sentLedger[id] = UnixNow();
            PruneLedger();
            SaveLedger();
        }
    }

    int LedgerCount()
    {
        EnsureLedgerLoaded();
        lock (ledgerLock)
        {
            return sentLedger.Count;
        }
    }

    void PruneLedger()
    {
        int hours = Configuration.DedupExpireHours;
        if (hours <= 0)
        {
            return;
        }
        long cutoff = UnixNow() - hours * 3600L;
        foreach (string id in sentLedger.Where(entry => entry.Value < cutoff).Select(entry => entry.Key).ToArray())
        {
            sentLedger.Remove(id);
        }
    }

    // 调用方都在 ledgerLock 里
    void SaveLedger()
    {
        string folder = ImageFolderPath();
        Directory.CreateDirectory(folder);
        JsonObject record = new();
        foreach (KeyValuePair<string, long> entry in sentLedger)
        {
            record[entry.Key] = entry.Value;
        }
        File.WriteAllText(LedgerPath, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    string DedupWindowText() => Configuration.DedupExpireHours <= 0 ? "永久以来" : Configuration.DedupExpireHours + "小时以来";

    static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ============================================================
    // 下载与缓存清理
    // ============================================================

    sealed record PixivFile(string Path, bool FromCache);

    string ImageFolderPath() => Path.Combine(AlifePath.StorageFolderPath, Configuration.ImageFolder.Trim());

    void CleanupCache()
    {
        string folder = ImageFolderPath();
        if (Directory.Exists(folder) == false)
        {
            return;
        }
        int limit = Math.Clamp(Configuration.MaxCacheFiles, 1, 1000);
        List<string> images = Directory.GetFiles(folder)
            .Where(path => IsImage(Path.GetExtension(path)))
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ToList();
        if (images.Count <= limit)
        {
            return;
        }
        int remove = Math.Min(Math.Clamp(Configuration.CleanupCount, 1, 100), images.Count - limit);
        int deleted = 0;
        foreach (string path in images.Take(remove))
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception deleteError)
            {
                // 刚发出去的文件协议端可能还攥着，删不动就留着，下次再清
                logger.LogInformation("[Pixiv] 旧图 {Path} 删不掉：{Error}", path, deleteError.Message);
            }
        }
        logger.LogInformation("[Pixiv] 缓存清理：删掉 {Deleted} 张旧图，剩 {Left} 张", deleted, images.Count - deleted);
    }

    static bool IsImage(string extension) => extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp";

    async Task<PixivFile> DownloadAsync(string id, int page)
    {
        string folder = ImageFolderPath();
        Directory.CreateDirectory(folder);
        foreach (string cached in Directory.EnumerateFiles(folder, id + "-" + page + ".*"))
        {
            if (new FileInfo(cached).Length > 0)
            {
                return new PixivFile(cached, true);
            }
        }

        // 镜像扩展名只是占位：实测回的类型跟着原作品走（PNG作品就回 image/png），真类型只能看 Content-Type。
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
        MarkSent(id);
        long bytes = new FileInfo(file.Path).Length;
        List<string> report = new()
        {
            "✅ 图片已发送：" + (hit == null ? "pixiv " + id : hit.Title + "／" + hit.UserName),
            "　" + id + " 第" + page + "页，" + SizeText(bytes) + (file.FromCache ? "，本地缓存，没重新下载" : "")
                + "，" + ArtworkPageBase + id + "。这张在 " + DedupWindowText() + "内不会再被搜到。"
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
        return await SendImageResultAsync(action, parameters, segments.Count);
    }

    // 多张图合成一条合并转发，防刷屏；三端都在动作表里注册了 send_forward_msg（本机安装包实测），但没实机验过，失败就退回逐条
    async Task<(bool Success, string Message)> SendForwardAsync(long target, bool isGroup, List<string> filePaths)
    {
        await ResolveProviderAsync();
        JsonArray nodes = new();
        foreach (string path in filePaths)
        {
            nodes.Add(new JsonObject
            {
                ["type"] = "node",
                ["data"] = new JsonObject
                {
                    ["name"] = selfNick,
                    ["uin"] = selfId,
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "image", ["data"] = new JsonObject { ["file"] = path } }
                    }
                }
            });
        }
        JsonObject parameters = new()
        {
            [isGroup ? "group_id" : "user_id"] = target,
            ["messages"] = nodes
        };
        return await SendImageResultAsync("send_forward_msg", parameters, nodes.Count);
    }

    async Task<(bool Success, string Message)> SendImageResultAsync(string action, JsonObject parameters, int imageCount)
    {
        (int retcode, string message, JsonElement data) = await SendActionAsync(action, parameters);
        if (retcode != 0)
        {
            return (false, ProviderLabel() + " 返回 " + retcode + "：" + message + FailureHint(retcode));
        }
        // 三端成功都带 message_id；LLOneBot 对不认识的段不报错直接丢，所以只认 retcode 不够
        if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("message_id", out _) == false)
        {
            return (false, ProviderLabel() + " 回成功但没给 message_id，" + imageCount + " 条图片段可能被静默丢弃");
        }
        return (true, "");
    }

    string FailureHint(int retcode) => retcode switch
    {
        1404 => "（" + ProviderLabel() + " 未实现该接口）",
        1400 or 1200 => "（" + ProviderLabel() + " 不认识这个消息段，整条已退回；image/node 是 OneBot 标准段，多半是本地文件路径读不到）",
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
        // 合并转发的 node 段要填机器人自己的号与昵称，别的端不一定实现 get_login_info，拿不到就只影响转发
        (int loginRetcode, string loginMessage, JsonElement login) = await SendActionAsync("get_login_info", new JsonObject());
        if (loginRetcode != 0)
        {
            logger.LogInformation("[Pixiv] get_login_info 返回 {Retcode}：{Message}，合并转发的节点署名会留空", loginRetcode, loginMessage);
            return;
        }
        selfId = GetText(login, "user_id");
        selfNick = GetText(login, "nickname");
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

    // pixiv.net 的接口（搜图和作品详情）本机实测国内直连不通，取图的镜像才不需要代理
    string RequireProxy()
    {
        string proxy = Configuration.ProxyUrl.Trim();
        if (proxy.Length == 0)
        {
            throw new Exception("Pixiv 搜图要走 pixiv.net，本机实测国内直连不通，得在插件配置「搜图·代理地址」里填一个HTTP代理（如 http://127.0.0.1:7890）；用户只给了作品ID或链接的话，直接 PixivSend 就不需要代理");
        }
        return proxy;
    }

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
        string folder = ImageFolderPath();
        if (Directory.Exists(folder) == false)
        {
            return folder + "（还没建，第一次取图时自动创建）";
        }
        long bytes = 0;
        int count = 0;
        foreach (string file in Directory.GetFiles(folder).Where(path => IsImage(Path.GetExtension(path))))
        {
            bytes += new FileInfo(file).Length;
            count++;
        }
        return folder + "（图片 " + count + " 个，" + SizeText(bytes) + "，上限 "
            + Math.Clamp(Configuration.MaxCacheFiles, 1, 1000) + " 个，超了每次删 "
            + Math.Clamp(Configuration.CleanupCount, 1, 100) + " 个）";
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
