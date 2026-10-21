# Suran.Pixiv

免登录搜 Pixiv 作品并把**原图**以QQ图片消息发出。全程不需要 Pixiv 账号：搜索走 pixiv.net 的公开接口，取图走 pixiv.re 镜像。

## 版本
4.1.1（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 外部：按关键词搜图需要一个 HTTP 代理（配置项「搜图·代理地址」，如 `http://127.0.0.1:7890`）；**取图发图不用代理**，但配了代理才能在读详情时过分级与排除标签那道检查

## AI 函数
| 函数 | 说明 |
|---|---|
| PixivSearch(keyword, count=8, order?) | 搜作品候选，返回 `作品ID 标题／作者 宽x高 页数 收藏 标签`。`order` 留空或 `popular` 按收藏数从多到少（本地排，慢），`new` 只要最新上传（快）；count 最多 20 |
| PixivSend(source, targetId?, type?, page?) | 发一张原图。`source` 可以是作品ID、作品链接（`/artworks/{id}`、`/en/artworks/{id}`、`/illust/{id}`），也可以是关键词；`page` 是多P作品的第几页，从1开始。**给ID或链接时也会先读一次详情过同一套闸门**（R-18、排除标签），配了代理才读得到，没配代理就直接发、回报里写明这次没做检查 |
| PixivSendMulti(keyword, targetId?, count=3, type?, order?) | 一次发几张，要得再多也会被配置「发送·一次最多几张」（默认6）截住，截了几张在回报里说。配置开了合并转发就一条消息发出，失败自动退回逐条 |
| PixivArtist(source, count=10) | 同一个画师的其他作品。`source` 给作品ID或链接最准，只给关键词就先搜一条再顺着它带出画师。同作者列表实测只给一堆作品ID，所以按ID从新到旧逐条开详情补齐标题/标签/收藏数并过一遍闸门，核几条的上限就是配置「搜图·抽几条读详情」，没轮上的只列ID |
| PixivEnv() | 自检：直连与经代理通不通、搜索接口实跑、镜像通不通、筛选与去重状况、R-18放行开关、图片目录与缓存上限、一次最多几张、协议端是哪个端、机器人自身账号 |

典型用法：`PixivSearch("初音ミク")` 看候选 → 挑一个 → `PixivSend("150881922", 群号)`。用户直接给ID或链接时不用搜索，不配代理也能发。

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| WsUrl | `ws://127.0.0.1:3001` | OneBot 正向 WebSocket，图片消息从这里发出 |
| ProxyUrl | 空 | 访问 pixiv.net 要用的 HTTP 代理。国内直连不通，按关键词搜图必填；只发已知作品ID不用填（填了才会读详情过闸门） |
| ExcludeTags | `AI,R-18,R-18G,裸足` | 包含式匹配（填 `AI` 也挡得住 `AIイラスト`）。搜索、画师列表、按ID直发三处都过这一道 |
| MinBookmarks | 0 | 0=不筛。收藏数只在详情接口里给，设了门槛就要逐条读详情，一次搜索多花十几秒；免登录只搜得到最新上传，门槛设上百基本全军覆没 |
| PopularPages | 3 | 按收藏数排时先翻几页当候选池（实测每页约59条、页与页不重叠）。免登录没有服务端人气榜，只能多翻再本地排 |
| ProbeSampleCount | 12 | 候选池按步长抽几条去读真实收藏数；PixivArtist 列同画师作品时也用它的上限逐条核验 |
| KeywordMap | 空 | 中文关键词转 P站真实 tag，格式 `中文=日文`，多条分号分隔；留空只用内置 26 条 |
| AllowR18 | 关 | 关：按ID或链接直发时 `xRestrict≠0` 直接拒发（1=R-18、2=R-18G 这套对应关系照参考插件的判定写的，本机免登录只实测到 0）。开：这类照发 |
| MaxBatchCount | 6 | PixivSendMulti 的条数上限（1-10），调用方要得再多也按这个数截住 |
| SendAsForward | 关 | 一次发多张合成一条合并转发；失败自动退回逐条。三端都注册了 `send_forward_msg`，但未实机验证 |
| DedupExpireHours | 24 | 发过的作品在这个时限内不再出现在搜索结果里；0=永久不再发。按ID直发撞上发过的只提示不拦 |
| MaxCacheFiles | 50 | 图片目录超过这个数就先删最旧的，只动图片扩展名的文件 |
| CleanupCount | 20 | 超上限时一次删几个 |
| ImageFolder | `Pixiv` | 相对 Alife Storage 的图片目录，同一作品同一页不重复下载 |
| SearchTimeoutSeconds | 20 | 一次搜索请求超时 |
| DownloadTimeoutSeconds | 60 | 镜像给的是原图，体积可能十几MB |
| EnableSend | 开 | 关掉后 PixivSend / PixivSendMulti 不可用也不下载；PixivSearch 只查不发，不受影响 |

## 参考插件（`KiraAI_pixiv_image_searcher_plugin-main`）功能对照
| 参考里的功能 | 这一版 |
|---|---|
| 中文口头说法 → P站真实tag | 移植：内置 26 条（黑丝→黒タイツ、崩铁→崩壊:スターレイル 等），配置「关键词映射」可再加 |
| 排除标签 | 移植：包含式匹配（填 `AI` 也挡得住 `AIイラスト`），默认 `AI,R-18,R-18G,裸足`；4.1.1 起按ID直发也过这一道，并说命中了哪个词 |
| 最低收藏数过滤 | **兼容实现**：收藏数只在作品详情接口里给（实测搜索接口不回），所以改成按步长抽样、逐条读详情再比，抽几条由配置定 |
| 人气排序 | **兼容实现**：免登录接口忽略排序参数（实测），改成多翻几页当候选池、读真实收藏数后本地排；翻几页与抽几条都成了配置项 |
| 一次发多张 / 合并转发 | 移植：`PixivSendMulti` + OneBot `send_forward_msg`，失败退回逐条；参考的 `max_count` 就是这里的「发送·一次最多几张」 |
| 发过的不再推 | 移植：`Storage\Pixiv\sent_records.json` 台账，默认 24 小时内不再出现在搜索里，0=永久 |
| 本地缓存自动清理 | 移植：图片数超过上限时先删最旧的 N 个，只动图片扩展名的文件，台账与非图片不碰 |
| 按画师取作品 | **兼容实现**：画师页与画师作品接口免登录都取不到（实测 404/400），改成顺着作品详情的 `userIllusts` 带出来 |
| R-18 分场景放行 | **兼容实现**：参考按会话白名单（`r18_whitelist` + `allow_r18_in_dm`）放行，Alife 这边没有那套会话概念，就做成一个全局开关「发送·放行R-18」，默认关；评级取自详情接口的 `xRestrict`（本机实测到的都是 0，1/2 的对应关系照参考插件 main.py 的判定搬的） |
| 原图/预览切换 | 不做：镜像只给原图，`i.pximg.net`（原图与预览都在它上面）本机直连超时、经代理带 Referer 也取不到（实测 403/404） |

## 实测链路（2026-10-15 本机 curl + 反射探针，2026-10-21 复跑 212 项 0 失败）
- 搜索：`GET https://www.pixiv.net/ajax/search/artworks/{关键词}?word=…&order=descend_date&sort_order=desc&p={页}&target=all&search_target=illust`
  - **免登录可用**，每页约 59 条；`body.illustManga.data[]` 字段有 `id/title/userName/width/height/pageCount/illustType/xRestrict/tags/url/createDate`
  - **排序参数一律被忽略**：`order=popular`、`order=popular_desc`、`s_mode=desc`、`order=descend_date` 回的内容完全一样，都是最新上传；`p=1..3` 各约 59 条且互不重叠，越翻越早
  - 人气榜与排行榜接口都没了：`ranking.php?mode=day&format=json` 回「ランキングが見つかりません」，`ranking.php?mode=day` 直接 404，`ajax/search/popular_naive`、`ajax/search/related`、`ajax/search/tag`、`ajax/search/illust` 全部「ページが見つかりません」
  - 数组里**会混进 `{"isAdContainer":true}` 这种广告位**（没有 `id`、没有 `url`），按"没有 id 就跳过"处理
  - `tags` 在这个接口里是**字符串数组**，不是 `{tag:...}` 对象数组；**不给收藏数**
  - **直连不通**（curl 回 000），必须经代理
- 详情：`GET https://www.pixiv.net/ajax/illust/{id}` 免登录可用（要代理），给 `illustTitle/userName/width/height/pageCount/illustType/tags[{tag}]/bookmarkCount/xRestrict/userIllusts/urls.original`
  - `tags` 这里是 `{tags:{tags:[{tag:…}]}}` 套两层，与搜索接口形状不同
  - `bookmarkCount` 是真实收藏数（实测当天新图 2~49 不等），也是唯一能拿到它的地方
  - `userIllusts` 实测是个 `{作品ID: null}` 的表（样本 119 条），**标题标签都不给**，只能逐条开详情——所以画师列表只核前 `min(要几条, 抽几条读详情)` 条，剩下的只列ID
  - `xRestrict` 实测正常作品为 0；未登录搜索接口回的一整页样本里也全是 0
  - `urls.original` 指向 `i.pximg.net`，本机**直连超时**（000），经代理不带 Referer 回 403、带 Referer 回 404，所以不能用它取原图
- 画师：`https://www.pixiv.net/users/{id}` 免登录 404、`ajax/user/{id}/illusts` 回 400，都取不到
- 取图：`GET https://pixiv.re/{id}.jpg`（第2页起 `{id}-{N}.jpg`）
  - **国内直连可用**，经代理也通；实测连取 5 次不限流
  - **第 1 页只能用 `{id}.jpg`**：实测 `{id}-1.jpg` 对部分单页作品直接 404
  - **URL 里的扩展名只是占位**，真类型只能看响应的 `Content-Type`（实测镜像回的是原作品类型，PNG 作品就回 `image/png`），插件据此命名本地文件（认 jpeg/png/gif/webp，其他直接报错）
  - 返回的是**原图**，实测 0.25 MB ~ 7.7 MB；400 = URL 形式不对，404 = 作品不存在或没有那一页
  - 动图（`illustType≠0`）与 Ugoira 只能拿到**静帧**
- 协议端：`get_version_info` 认端，`get_login_info` 拿机器人自身 ID/昵称给合并转发节点署名（拿不到不影响发送）；`send_forward_msg` 与 `node` 段三端源码里都注册过动作，**未实机验证**
- 本地缓存：文件落 `Storage\Pixiv\{id}-{页码}.{真实扩展名}`，同一作品同一页第二次直接复用，不再请求镜像

### 4.1.1 另加的实测（2026-10-21 本机 curl + 探针复跑）
- `xRestrict` 两处都有：搜索样本 60 条全为 0（唯一没有该字段的是无ID广告位，按"没有 id 就跳过"一起滤掉了），详情样本 `150882915` 为 0；插件按 1=R-18、2=R-18G、其他非0值原样报 `xRestrict=N`
- 免登录取不到 R-18 作品的详情，所以闸门这一道只有用户自己给ID时才会撞上——探针用构造的 PixivHit 验放行与拒发逻辑，真实R-18作品本机取不到、**没有验过**
- `i.pximg.net` 直连 12 秒超时（带不带 Referer 都是 000）；经代理不带 Referer 403、带 Referer 404（样本是 10-15 那天的图，现已过期，但 403 那条足够说明免登录取不到）

## 内容安全
未登录的搜索接口本身就不会返回 R-18 与私有作品；插件仍然会跳过 `xRestrict≠0` 的条目，并按「排除标签」再挡一层（默认含 R-18、R-18G）。4.1.1 起这套判定不再只管搜索：**按ID或链接直发也会先读详情过一遍**，拦下发一条说明（是哪个标签命中了哪个排除词、还是分级为 R-18/R-18G），放行要改「搜图·排除标签」或开「发送·放行R-18」；没配代理时读不到详情，就照发并在回报里写明这次没做检查。

## 注意事项
- **`image` / `node` 消息段在 NapCat / LLOneBot / SnowLuma 三端未做实机验证**。三端差异只源码核对过：成功一律要认 `data.message_id`（LLOneBot 对不认识的段不报错直接丢，仍回 retcode 0），不认识的接口回 1404，NapCat 1200 / SnowLuma 1400 会整条退回
- 免登录看不到全站热图：`order` 的"人气"是在能翻到的这几页最新上传里排，用户要经典热图请让他给作品ID或链接
- 收藏数门槛别设高：候选都是新上传，门槛设上百基本全军覆没（默认 0=不筛）
- 开了「搜图·最低收藏数」或不填 `order=new` 时，一次搜索要多打十几个请求，慢是预期的；想快就调小「搜图·抽几条读详情」，想排得准就调大「搜图·人气翻几页」
- 按ID直发的分级检查依赖代理：没配 ProxyUrl 时这道检查做不了，回报会写明白，别当成"已经审过了"
- 合并转发默认关，先在群里确认端支持再开
