# Suran.Pixiv

免登录搜 Pixiv 作品并把**原图**以QQ图片消息发出。全程不需要 Pixiv 账号：搜索走 pixiv.net 的公开接口，取图走 pixiv.re 镜像。

## 版本
4.1.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 外部：按关键词搜图需要一个 HTTP 代理（配置项「搜图·代理地址」，如 `http://127.0.0.1:7890`）；**发图取图不用代理**

## AI 函数
| 函数 | 说明 |
|---|---|
| PixivSearch(keyword, count=8, order?) | 搜作品候选，返回 `作品ID 标题／作者 宽x高 页数 收藏 标签`。`order` 留空或 `popular` 按收藏数从多到少（本地排，慢），`new` 只要最新上传（快）；count 最多 20 |
| PixivSend(source, targetId?, type?, page?) | 发一张原图。`source` 可以是作品ID、作品链接（`/artworks/{id}`、`/en/artworks/{id}`、`/illust/{id}`），也可以是关键词；`page` 是多P作品的第几页，从1开始 |
| PixivSendMulti(keyword, targetId?, count=3, type?, order?) | 一次发几张（最多6）。配置开了合并转发就一条消息发出，失败自动退回逐条 |
| PixivArtist(source, count=10) | 同一个画师的其他作品ID。`source` 给作品ID或链接最准，只给关键词就先搜一条再顺着它带出画师 |
| PixivEnv() | 自检：直连与经代理通不通、搜索接口实跑、镜像通不通、筛选与去重状况、图片目录与缓存上限、协议端是哪个端、机器人自身账号 |

典型用法：`PixivSearch("初音ミク")` 看候选 → 挑一个 → `PixivSend("150881922", 群号)`。用户直接给ID或链接时不用搜索，也就完全不需要代理。

## 参考插件（`KiraAI_pixiv_image_searcher_plugin-main`）功能对照
| 参考里的功能 | 这一版 |
|---|---|
| 中文口头说法 → P站真实tag | 移植：内置 26 条（黑丝→黒タイツ、崩铁→崩壊:スターレイル 等），配置「关键词映射」可再加 |
| 排除标签 | 移植：包含式匹配（填 `AI` 也挡得住 `AIイラスト`），默认 `AI,R-18,R-18G,裸足` |
| 最低收藏数过滤 | 移植：收藏数只在作品详情接口里给，按步长最多取 12 条样本逐条读 |
| 人气排序 | **兼容实现**：免登录接口忽略排序参数（实测），改成多翻 3 页当候选池、读真实收藏数后本地排 |
| 一次发多张 / 合并转发 | 移植：`PixivSendMulti` + OneBot `send_forward_msg`，失败退回逐条 |
| 发过的不再推 | 移植：`Storage\Pixiv\sent_records.json` 台账，默认 24 小时内不再出现在搜索里，0=永久 |
| 本地缓存自动清理 | 移植：图片数超过上限时先删最旧的 N 个，只动图片扩展名的文件，台账与非图片不碰 |
| 按画师取作品 | **兼容实现**：画师页与画师作品接口免登录都取不到（实测 404/400），改成顺着作品详情的 `userIllusts` 带出来 |
| R-18 分场景开关 | 不做：未登录搜索接口本身就不回 R-18（实测全是 `xRestrict=0`），没有可控的东西 |
| 原图/预览切换 | 不做：镜像只给原图，`i.pximg.net`（原图与预览都在它上面）免登录取不到 |
| `/pixiv` 斜杠命令 | 不做：Alife 框架没有消息前置钩子，命令注册不了，全部走 AI 函数 |

## 实测链路（2026-10-15 本机 curl + 反射探针 155 项）
- 搜索：`GET https://www.pixiv.net/ajax/search/artworks/{关键词}?word=…&order=descend_date&sort_order=desc&p={页}&target=all&search_target=illust`
  - **免登录可用**，每页约 59 条；`body.illustManga.data[]` 字段有 `id/title/userName/width/height/pageCount/illustType/xRestrict/tags/url/createDate`
  - **排序参数一律被忽略**：`order=popular`、`order=popular_desc`、`s_mode=desc`、`order=descend_date` 回的内容完全一样，都是最新上传；`p=1..3` 各约 59 条且互不重叠，越翻越早
  - 人气榜与排行榜接口都没了：`ranking.php?mode=day&format=json` 回「ランキングが見つかりません」，`ranking.php?mode=day` 直接 404，`ajax/search/popular_naive`、`ajax/search/related`、`ajax/search/tag`、`ajax/search/illust` 全部「ページが見つかりません」
  - 数组里**会混进 `{"isAdContainer":true}` 这种广告位**（没有 `id`、没有 `url`），按"没有 id 就跳过"处理
  - `tags` 在这个接口里是**字符串数组**，不是 `{tag:...}` 对象数组；**不给收藏数**
  - **直连不通**（curl 回 000），必须经代理
- 详情：`GET https://www.pixiv.net/ajax/illust/{id}` 免登录可用（要代理），给 `illustTitle/userName/width/height/pageCount/illustType/tags[{tag}]/bookmarkCount/userIllusts/urls.original`
  - `tags` 这里是 `{tags:{tags:[{tag:…}]}}` 套两层，与搜索接口形状不同
  - `bookmarkCount` 是真实收藏数（实测当天新图 2~49 不等），也是唯一能拿到它的地方
  - `userIllusts` 是 `{作品ID:{…}}` 对象，实测同作者 20 条以上，含标题、标签、尺寸、页数
  - `urls.original` 指向 `i.pximg.net`，本机**直连不通**，所以不能用它取原图
- 画师：`https://www.pixiv.net/users/{id}` 免登录 404、`ajax/user/{id}/illusts` 回 400，都取不到
- 取图：`GET https://pixiv.re/{id}.jpg`（第2页起 `{id}-{N}.jpg`）
  - **国内直连可用**，经代理也通；实测连取 5 次不限流
  - **第 1 页只能用 `{id}.jpg`**：实测 `{id}-1.jpg` 对部分单页作品直接 404
  - **URL 里的扩展名只是占位**，真类型只能看响应的 `Content-Type`（实测镜像回的是原作品类型，PNG 作品就回 `image/png`），插件据此命名本地文件（认 jpeg/png/gif/webp，其他直接报错）
  - 返回的是**原图**，实测 0.25 MB ~ 7.7 MB；400 = URL 形式不对，404 = 作品不存在或没有那一页
  - 动图（`illustType≠0`）与 Ugoira 只能拿到**静帧**
- 协议端：`get_version_info` 认端，`get_login_info` 拿机器人自身 ID/昵称给合并转发节点署名（拿不到不影响发送）；`send_forward_msg` 与 `node` 段三端源码里都注册过动作，**未实机验证**
- 本地缓存：文件落 `Storage\Pixiv\{id}-{页码}.{真实扩展名}`，同一作品同一页第二次直接复用，不再请求镜像

## 内容安全
未登录的搜索接口本身就不会返回 R-18 与私有作品；插件仍然会跳过 `xRestrict≠0` 的条目，并按「排除标签」再挡一层（默认含 R-18、R-18G）。

## 注意事项
- **`image` / `node` 消息段在 NapCat / LLOneBot / SnowLuma 三端未做实机验证**。三端差异只源码核对过：成功一律要认 `data.message_id`（LLOneBot 对不认识的段不报错直接丢，仍回 retcode 0），不认识的接口回 1404，NapCat 1200 / SnowLuma 1400 会整条退回
- 免登录看不到全站热图：`order` 的"人气"是在能翻到的这几页最新上传里排，用户要经典热图请让他给作品ID或链接
- 收藏数门槛别设高：候选都是新上传，门槛设上百基本全军覆没（默认 0=不筛）
- 开了「搜图·最低收藏数」或不填 `order=new` 时，一次搜索要多打十几个请求，慢是预期的
- 合并转发默认关，先在群里确认端支持再开
