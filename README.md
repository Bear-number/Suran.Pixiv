# Suran.Pixiv

免登录搜 Pixiv 作品并把**原图**以QQ图片消息发出，移植自工作区的 `pixiv-image-searcher` 技能。三个 LLM 工具：搜图看候选、按关键词或作品ID发图、自检环境。全程不需要 Pixiv 账号。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller
- 外部：按关键词搜图需要一个 HTTP 代理（配置项「搜图·代理地址」，如 `http://127.0.0.1:7890`）；**发图取图不用代理**

## AI 函数
| 函数 | 说明 |
|---|---|
| PixivSearch(keyword, count=8, order?) | 搜作品，返回 `作品ID 标题／作者 宽x高 页数 标签`。`order` 留空按人气、填 `new` 按最新，count 最多 20 |
| PixivSend(source, targetId?, type?, page?) | 发一张原图。`source` 可以是作品ID、作品链接（`/artworks/{id}`、`/en/artworks/{id}`），也可以是关键词（就发搜到的第一条人气结果）；`page` 是多P作品的第几页，从1开始 |
| PixivEnv() | 自检：pixiv.net 直连与经代理通不通、搜索接口实跑一条关键词的结果、pixiv.re 镜像通不通、图片目录缓存了多少、协议端是哪个端 |

典型用法：`PixivSearch("初音ミク")` 看候选 → 挑一个 → `PixivSend("150881922", 群号)`。用户直接给ID或链接时不用搜索，也就完全不需要代理。

## 实测链路（2026-10-15 本机 curl + 反射探针）
- 搜索：`GET https://www.pixiv.net/ajax/search/artworks/{关键词}?word=…&order=popular|descend_date&sort_order=desc&p=1&target=all&search_target=illust`
  - **免登录可用**，每页 60 条；`body.illustManga.data[]` 字段有 `id/title/userName/width/height/pageCount/illustType/xRestrict/tags/url`
  - 数组里**会混进 `{"isAdContainer":true}` 这种广告位**（没有 `id`、没有 `url`），插件按"没有 id 就跳过"处理
  - `tags` 在这个接口里是**字符串数组**，不是 `{tag:...}` 对象数组
  - **直连不通**（curl 回 000），必须经代理。技能里写的"用搜索引擎搜 `site:pixiv.net/artworks/`"这条路本机也测了：Bing（含 cn.bing.com）、Sogou、Baidu 直连**一条 pixiv 链接都搜不到**，DuckDuckGo HTML 能出 10 条但同样要代理——所以不存在"免代理搜图"的办法，插件直接改走 pixiv 自己的公开接口
- 详情：`GET https://www.pixiv.net/ajax/illust/{id}` 免登录可用（要代理），只在按ID发图时用来补标题与总页数；它给的 `urls.original` 指向 `i.pximg.net`，本机**直连不通**，所以不能用它取原图
- 取图：`GET https://pixiv.re/{id}.jpg`
  - **国内直连可用**，经代理也通；实测连取 5 次不限流
  - 页码形式是 `{id}-{N}.jpg`（N 从 1 开始），但**第 1 页只能用 `{id}.jpg`**：实测 `{id}-1.jpg` 对部分单页作品直接 404，而 `{id}.jpg` 对单页和多页作品都返回第 1 页
  - **URL 里的扩展名只是占位**：实测 `.png`、`.gif` 都回同一份 JPEG，所以真类型只能看响应的 `Content-Type`，插件据此命名本地文件（认 jpeg/png/gif/webp，其他类型直接报错）
  - 返回的是**原图**，实测 0.25 MB ~ 7.7 MB；400 = URL 形式不对，404 = 作品不存在或没有那一页
  - 动图（`illustType≠0`）与 Ugoira 只能拿到**静帧**（实测回 JPEG）
- 本地缓存：文件落 `Storage\Pixiv\{id}-{页码}.{真实扩展名}`，同一作品同一页第二次直接复用，不再请求镜像

## 内容安全
未登录的搜索接口本身就不会返回 R-18 与私有作品；插件仍然会跳过 `xRestrict≠0` 的条目，按ID直接发时也会先看详情接口的 `xRestrict`。

## 与技能版的差异
- 技能让模型自己去搜 `site:pixiv.net/artworks/` 再 curl 一条 ID；插件改成一次拿回标题、作者、尺寸、页数、标签的候选列表，能挑、能翻页、能复用缓存
- 技能里"pixiv.cat 国内已封"实测成立，`pixiv.re` 直连可用，所以只用后者
- 技能没有失败说明；插件会把"没填代理""镜像404=没有那一页""回的不是可识别图片"这些情况直接写成中文告诉模型

## 注意事项
- **`image` 消息段在 NapCat / LLOneBot / SnowLuma 三端未做实机验证**（三端差异只源码核对过 `json`/`music` 段与历史消息）。发送走 OneBot v11 标准 `{"type":"image","data":{"file":"绝对路径"}}`，且必须拿到 `data.message_id` 才算成功——LLOneBot 对不认识的段会静默丢掉还回 retcode 0。第一次用请先发给自己或小群确认
- `PixivSearch` 不给图片预览链接：搜索结果里的 `url` 是 `i.pximg.net` 的缩略图，本机直连不通，给了也打不开
- 取到的图不会自动删，目录长大会占盘，清理请自己删 `Storage\Pixiv`
- `PixivEnv` 里"pixiv.net 直连：不通"是**预期结果**，不是故障；只看"经代理"那一行
- 插件自己维护与协议端的 WebSocket 连接（配置「协议端地址」），与 Suran.QQMedia / Suran.ShareArk 同一套传输
