# Emby 单集简介翻译插件（Overview Translator）

给 Emby 的**剧集简介**做中文翻译，翻译完写回 Emby 数据库。
翻译引擎可插拔：DeepSeek、任意 OpenAI 兼容接口、以及 4 个免密钥的在线翻译。

面向的场景很具体：用 **Bangumi** 刮削动画，标题有中文，**简介却仍是日文**。

> English TL;DR: An Emby plugin that translates episode *overviews* into your target language and
> writes them back to the Emby database. Pluggable engines (DeepSeek / any OpenAI-compatible API /
> keyless Google, Bing, MyMemory / self-hosted LibreTranslate). Built for Emby 4.10+ (net8.0) and
> 4.9.x (net6.0). Install the DLL into Emby's `programdata/plugins`, restart Emby, configure it in
> the left menu under 「简介翻译」. Licensed GPL-2.0.

- 只改 **Overview 一个字段**：不碰媒体文件、不写 NFO、不动标题/季集号。
- 只写 Emby 数据库：调用 Emby 自己的条目更新接口。
- 三种触发方式：定时任务、新入库自动翻译、浏览器用户脚本按钮。

---

## 目录

- [解决什么问题](#解决什么问题)
- [兼容性矩阵](#兼容性矩阵)
- [安装](#安装)
- [配置](#配置)
- [三种触发方式](#三种触发方式)
- [它是怎么工作的](#它是怎么工作的)
- [已知限制（请先读）](#已知限制请先读)
- [为什么不直接用现成插件](#为什么不直接用现成插件)
- [自己编译](#自己编译)
- [HTTP 接口](#http-接口)
- [排错](#排错)
- [验证记录](#验证记录)
- [许可证与贡献](#许可证与贡献)

---

## 解决什么问题

如果你用 Bangumi 刮削动画，可能会发现：**标题是中文，简介却全是日文**，而且 Emby 设置里
「首选元数据语言」怎么调都没用。

原因有两条：

1. **Emby 的「首选元数据语言」不会触发翻译。** 它只是告诉提供器"优先要哪种语言的元数据"，
   提供器给什么就是什么，Emby 不会去翻译。
2. **Bangumi 的 `TranslationPreference=Chinese` 只管标题，不管简介。** Bangumi 的 OpenAPI
   没有语言参数，`summary` / `desc` 字段返回什么就是什么（大多是日文）；Bangumi 插件里
   `Summary => SummaryRaw?.ToMarkdown()` 是**原样搬运**，中文偏好只作用在 `name_cn`（标题）上。

所以这不是配置问题，光调设置调不出来——需要一个**事后翻译**的环节。本插件就是干这个的。

---

## 兼容性矩阵

Emby 的插件 DLL 与宿主运行时绑定，**装错产物会加载失败**。请按 Emby 版本选：

| Emby Server 版本 | 宿主运行时 | 应该安装的产物 | 说明 |
|---|---|---|---|
| **4.10 及以上**（含 4.10.1.0） | .NET 8 | `net8.0/Emby.Plugin.OverviewTranslator.dll` | 本插件的主要验证目标 |
| **4.9.x 及更早** | .NET 6 | `net6.0/Emby.Plugin.OverviewTranslator.dll` | 用同一份源码编译，未在真实 4.9 上端到端验证（见[已知限制](#已知限制请先读)第 12 条） |

怎么确认自己的 Emby 是哪个：Emby 控制台 → 「关于」看版本号；
4.10 起服务端跑在 .NET 8 上，4.9 及更早是 .NET 6。

本仓库同时产出两个目标框架的 DLL，CI 也会把两个都作为 artifact 上传。

---

## 安装

### 前置条件

编译才需要 .NET SDK 和 Emby 程序集；**只安装现成 DLL 的话什么都不需要**。

如果你要自己编译，见[自己编译](#自己编译)。本节假设你已经拿到（或编译出）了
`Emby.Plugin.OverviewTranslator.dll`。

### 步骤

1. **关掉/准备重启 Emby。** 正在被加载的 DLL 在 Windows 上无法直接覆盖，
   Emby 会锁住 `plugins` 目录里的文件。
2. **把 DLL 放进 Emby 的 plugins 目录**：

   | 平台 | 路径 |
   |---|---|
   | Windows | `%APPDATA%\Emby-Server\programdata\plugins\` |
   | Windows（以服务方式运行） | 多数情况下仍是上面那个 `%APPDATA%` 路径；不确定时到 Emby 控制台「关于」或日志 `embyserver.txt` 的启动行里确认 programdata 实际位置，plugins 子目录就在它下面 |
   | Linux（官方安装脚本） | `/var/lib/emby/plugins/` |
   | Docker | 容器内 `/config/plugins/`（对应宿主机映射的 config 卷） |

   注意是 **programdata 下面**的 `plugins`，不是 Emby 安装目录下的 `system`。
   文件名请保持 `Emby.Plugin.OverviewTranslator.dll` 不变（Emby 按程序集名识别插件）。

3. **重启 Emby**（控制台里点重启，或重启服务/容器）。
4. 打开 Emby 控制台 → **插件**，应能看到「Overview Translator」。
5. 左侧主菜单会多出一项 **「简介翻译」**；也可以直接访问
   `/web/configurationpage?name=overviewtranslator`。

安装不需要额外依赖：插件只引用 Emby 自带的程序集，**不会**向 plugins 目录复制任何
MediaBrowser.*.dll。

### 卸载 / 回滚

删掉 DLL 然后重启即可，不会影响 Emby 本体：

```powershell
# Windows 示例
Remove-Item "$env:APPDATA\Emby-Server\programdata\plugins\Emby.Plugin.OverviewTranslator.dll*"
```

插件的数据文件（删掉只会丢失翻译记忆，不会影响 Emby 库）：

```
<programdata>\plugins\configurations\Emby.Plugin.OverviewTranslator.xml   插件设置
<programdata>\plugins\configurations\overviewtranslator\
  cache.json   翻译记忆：原文哈希 → 译文（同一段文字只花一次钱）
  items.json   逐条翻译记录，用于审计
```

> 注意路径是 `plugins\configurations\`，**不是** `programdata\configurations\`。

---

## 配置

Emby 控制台 → 左侧 **「简介翻译」**。至少要设置这几项：

| 设置项 | 默认值 | 说明 |
|---|---|---|
| 启用 | 开 | 总开关，关掉后定时任务直接退出 |
| 翻译引擎 | DeepSeek | 见下方引擎表 |
| 源语言 | `auto` | 让引擎自己判断；免密钥引擎对 `auto` 支持不一，见引擎表 |
| 目标语言 | `zh-CN` | |
| 使用代理 | 关 | 国内直连 Google / DeepSeek 不通时需要开；默认 `127.0.0.1:7890`，**按自己的代理端口改** |
| 每次上限 | 500 条 | 防止第一次跑就把免费额度烧完 |

**先用「Google 免密钥」验证链路是通的**：它不需要任何 Key，配好代理点一下就能看到结果。
不想申请 Key 又只有动画的话，DeepSeek 最省事（便宜、支持 `auto`、批量翻译质量稳定）。

LLM 类引擎另有这些设置：`BaseUrl`（DeepSeek 预填 `https://api.deepseek.com/v1`）、
`ApiKey`、`Model`（默认 `deepseek-chat`）、`TimeoutSeconds`(120)、`BatchSize`(10)、
`Temperature`(1.3)、`MaxTokens`(8192)、`ExtraPrompt`。
DeepSeek 官方给翻译任务推荐的 `temperature` 就是 **1.3**，这里默认就是 1.3。

### 支持的引擎

| 引擎 | 需要 Key | 注意事项 |
|---|---|---|
| **DeepSeek** | 是 | 官方 API，`BaseUrl` 预填。支持 `auto`、支持一次请求翻多条（省钱）。**推荐**。 |
| **OpenAI 兼容** | 是 | OpenAI / 智谱 / 通义 / Ollama / one-api 等都行，填对应 `BaseUrl` + `Model`。 |
| **Google 免密钥** | 否 | 免密钥方便，但**会限流**：同一出口 IP 连续调用会返回 `429 Too Many Requests`。适合少量手工翻译，批量请用 LLM 引擎。国内需要代理。 |
| **Bing 免密钥** | 否 | **网页抓取**实现（走 bing.com 翻译页的 `ttranslatev3` 会话）。微软一改版就可能失效。失效时是单条可读错误，不会崩。 |
| **MyMemory 免密钥** | 否 | **不支持 `auto`**（会返回 `'AUTO' IS AN INVALID SOURCE LANGUAGE`）。翻日文必须把源语言显式设为 `ja`，否则它会把日文原样回传（插件会识别成失败，不会把原文当译文写回去）。单次 500 字符上限，超长会切块。 |
| **LibreTranslate** | 自建 | 填自己的实例地址（`LibreTranslateUrl`），可选 API Key。完全离线可控。 |

---

## 三种触发方式

三种方式可以只用一种，也可以组合。

### 1. 定时任务（补存量）

Emby 控制台 → **计划任务** → 「翻译剧集简介」→ 立即运行，或设定间隔后自动跑。

- 默认**不自动跑**（间隔 0）。
- 想定时执行：把「间隔分钟」设为非 0，**然后重启一次 Emby**
  （触发器的注册发生在启动时）。
- 首次装好、库里已经有一堆日文简介时，用这个方式一次性补齐。

### 2. 新入库 / 元数据刷新时自动翻译（日常维护）

在设置页勾选「新入库 / 元数据刷新时自动翻译」，之后新集入库或刷新完成时会自动翻译，
不用再管。

- **默认关闭**，因为每次自动翻译都可能花钱。
- 触发靠两个事件：`IProviderManager.RefreshCompleted`（主）和 `ILibraryManager.ItemAdded`（辅），
  然后经过**防抖延迟**（默认 **300 秒 / 5 分钟**）才开始翻译。
- 为什么必须延迟：Emby 的事件在元数据提供器**写完简介之前**就会触发；一次要访问
  Bangumi/TMDB 的刷新可能拖很久。等久一点只是新集晚几分钟有中文；等太短则会翻到空内容。
- 就算某次自动触发没赶上，下一次定时任务也会补上。

### 3. 浏览器用户脚本按钮（个别条目手动补翻）

**Emby 不会把插件脚本注入网页**（实测 `/web/index.html` 里没有任何插件脚本引用，
连官方 Bangumi 插件也没有），所以插件**无法**自己在详情页加按钮。
想要"点一下翻当前这一集"这个交互，只能从浏览器这一侧注入：

1. 安装 [Tampermonkey](https://www.tampermonkey.net/) 或 Violentmonkey。
2. 新建脚本，把本仓库 [`userscript/emby-overview-translator.user.js`](userscript/emby-overview-translator.user.js)
   的内容整个粘进去保存。
3. **改 `@match` 行**：默认只匹配 `http://localhost:8096/web/*` 和 `http://127.0.0.1:8096/web/*`。
   你的 Emby 若是局域网 IP 或域名，请自己加一行，例如
   `// @match http://192.168.1.10:8096/web/*`。
4. 打开单集详情页，右下角出现「翻译简介」按钮，点击即翻译当前条目。
   不想装扩展的话，也可以按 F12 把脚本贴进 Console（每次刷新都要重来）。

API Key 由脚本从 Emby 网页客户端已有的登录令牌里取，取不到才弹窗让你填一次；
它存在浏览器 localStorage 里，不会写进插件配置。

设置页里的「允许在单集页面显示按钮」**默认关闭**，而且它只是个开关——不装用户脚本时，
开与不开没有任何可见区别（Emby 侧没有注入点）。

---

## 它是怎么工作的

```
Plugin.cs                       插件入口（配置 + 设置页 IHasWebPages）
ServerEntryPoint.cs             注入 Emby 服务、注册计划任务
TranslationServiceHost.cs       静态宿主，持有唯一的翻译服务实例
Tasks/TranslateOverviewTask     计划任务「翻译剧集简介」
AutoTranslateOnLibraryEvents   新入库/刷新事件的防抖触发
Api/TranslationController       REST 接口（Settings/Translate/Batch/Status/Test）

Services/
  TranslationService           主流程：挑条目 → 翻译 → 写回 → 记账
  MetadataWriter               写回 Emby + "这已经是中文了吗"的语言判定
  EligibleItemEnumerator       找出候选条目（默认只取 Episode）
  TextTranslator               单条/批量翻译的调度与重试
Translation/
  TranslationBackendFactory    按配置造引擎；配置不可用时抛中文可读异常
  JsonTranslationStore         cache.json（翻译记忆）+ items.json（审计）
  HttpClientProvider           HTTP 客户端与代理集中在这里
  Backends/                    DeepSeek / OpenAI 兼容 / Google / Bing / MyMemory / LibreTranslate
  Backends/JsonPath.cs         容忍脏 JSON 的小型解析器
Configuration/configPage.html  设置页（嵌入式 HTML）
Web/overviewtranslator.js      设置页脚本（嵌入式）
```

几个刻意的设计决定：

- **不用 DI 注册。** Emby 4.10 的 `IApplicationHost` 没有 `RegisterService`，容器契约跨版本
  不稳定。改成静态宿主，控制器和任务都从它取；初始化顺序无关。
- **回写用 `ILibraryManager.UpdateItem(item, parent, ItemUpdateType.MetadataEdit)`。**
  Emby 里不存在 `UpdateItemAsync`（那是 Jellyfin 独有的扩展方法）。
- **不注册新的元数据提供器。** 翻译是"事后修正"，做成 `IMetadataProvider` 会和 Bangumi 的
  刷新抢时序。
- **翻译记忆。** 同一段原文 + 同一引擎 + 同一目标语言只翻一次，重跑不花钱。
- **空 Key 绝不发 `Authorization` 头。** 发 `Bearer `（空令牌）会被严格的 API 判 401。

---

## 已知限制（请先读）

这些是**如实记录**的行为，不是待办清单的广告。按影响排序：

1. **没有原生详情页按钮。** Emby 4.10 不会把插件脚本注入网页（见上文），所以按钮必须靠
   浏览器用户脚本。插件能自己提供的只有：设置页、计划任务、REST 接口、后台自动翻译。
2. **改「间隔分钟」需要重启 Emby** 才生效（触发器在启动时注册）。
3. **只翻 Episode。** 剧集/季级的简介通常已经是中文，所以默认只处理 `Episode`
   （配置里的 `ItemTypes`）。`EligibleItemEnumerator` 目前只按类型名筛选。
4. **非中文目标语言不做「已翻译」判定。** 只有目标语言是中文时才按汉字/拉丁字母比例判断，
   避免误跳过；翻成其它语言时每条都会翻（命中缓存仍不会重复收费）。
5. **不写 NFO。** 插件只调用 Emby 的条目更新接口。如果你装了 NfoMetadata 而且库开启了
   NFO 保存，那是 Emby 自己的行为，不是本插件写的。
6. **Bing 免密钥是网页抓取，天然脆弱。** 微软已下线 `edge.microsoft.com/translate/auth`
   （恒 404），插件改用 bing.com 翻译页自己调用的 `ttranslatev3` 会话。**微软改版就会失效**，
   失效时是一条可读错误并提示改用 Google/DeepSeek，不会崩。库里有动画之外的内容时才需要考虑它。
7. **`zh-CN` 与 `zh-TW` 在 Google/LibreTranslate 上都会变成 `zh`（简体）。** 语言码归一化时
   抹掉了地区码；只有 Bing 分支对 `zh-TW`/`zh-HK` 特判成 `zh-Hant`。要简体中文按默认用即可。
8. **MyMemory 超过 500 字符会切块**再逐块翻译拼接（它的单次上限），多块之间 CJK 直接相连、
   其它语言补空格。不切块的话长简介会 100% 失败。
9. **`SourceLanguage=auto` 对免密钥引擎是有损的。** MyMemory 不支持 auto，插件里它会退化成
   `en`，于是拿 `en|zh` 去翻日文，MyMemory 会把日文原样回传。插件会识别"译文与原文相同"
   并判为失败（不会静默把原文当译文写回、也不会污染缓存），但要真正翻日文请把源语言设成
   `ja`，或改用 Google / DeepSeek。
10. **Google 免密钥会限流**（429），见引擎表。
11. **插件构造函数无法在 Emby 之外执行。** `BasePlugin` 的 `AssemblyFilePath` 由 Emby 的插件
    管理器在加载时写入，所以任何外部宿主构造 `Plugin` 都会拿到 `Path.Combine(path, null)`。
    这只影响"脱离 Emby 做单元测试"，不影响插件运行。
12. **net6.0 目标没有在真实 Emby 4.9 上跑过。** 它经过了编译验证（0 警告 0 错误，用的是官方
    NuGet 上 Emby 4.9.1.90 的程序集），但**没有**在 4.9 宿主上做过端到端实测——手上只有
    4.10.1.0。装到 4.9 之前请自行评估。
13. **以下缺陷已知且刻意保留**（都只在异常/畸形输入下出现，修它们的代价大于收益）：
    - **JsonPath 的同辈键遮蔽**：响应里同时出现多个 `choices` / `message` 键时，可能取到诱饵值。
      真实 LLM 响应不会这样。
    - **响应被截断时可能返回半截译文**：没有完整性校验。
    - **语言启发式的边界**：纯汉字日文（无假名）会判为中文；中文里出现任一假名即判为日文；
      目标是日文时不做"已翻译"判定。
    - **`TextTranslator` 不捕获后端抛出的异常**：契约允许后端抛异常来中止调用，
      于是异常会冒泡到调用方（任务日志里能看到），而不是被转成一条"翻译失败"。
    - **行内 HTML 标签差异检测不到**：`<b>重要</b>通知` 与 `重要通知` 会被视为不同文本（影响极小）。
    - **模型末尾加杂讯会让整批作废**：提示词已明确要求不要输出多余行，模型不听话时该批报错重试。

---

## 为什么不直接用现成插件

因为生态里没有能直接用的：

- **MetaTube** 的自动翻译只在它自己刮削时生效，它连 Series/Episode 提供器都没有。
- **emby-toolkit** 是独立 Docker 服务，只翻它自己抓的 TMDb 数据。
- 其余搜索结果基本是字幕翻译，不是元数据翻译。

本插件不抢刮削的活——它只做"事后把日文简介换成中文"这一件事，因此可以和任何刮削插件共存。

---

## 自己编译

### 需要什么

- **.NET SDK 8.0**（要编 net8.0 目标就必须要 8.0 SDK）。编译 net6.0 目标时，8.0 SDK 会从
  NuGet 自动下载 .NET 6 的引用包（`Microsoft.NETCore.App.Ref` 6.0.x，**需要联网**），
  所以不必单独装 .NET 6 SDK；反过来装了 6.0.x + 8.0.x 两个 SDK 也可以。
- **已安装的 Emby Server 的 `system` 目录**——这是关键前置条件。Emby 是"部署好的应用"而不是
  NuGet 包，插件在编译期直接引用安装目录里随服务端发布的程序集
  （`MediaBrowser.Common.dll` / `MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll`）。
  没有 Emby，就没有这几个 DLL，也就无法编译。

  | 平台 | `system` 目录常见位置 |
  |---|---|
  | Windows | `%APPDATA%\Emby-Server\system` |
  | Linux | `/opt/emby-server/system` 或 `/var/lib/emby/system` |

  注意是 **system**（Emby 程序本体），和上面安装插件用的 **programdata\plugins** 是两个目录。

### 编译

```powershell
# 方式 A：让脚本自动探测 Emby 目录（Windows 默认位置 / Linux 常见位置）
pwsh ./build.ps1                  # PowerShell 7+
powershell -File ./build.ps1      # Windows 自带的 Windows PowerShell 5.1 也行

# 方式 B：显式指定
pwsh ./build.ps1 -EmbySystemDir "C:\Users\<你>\AppData\Roaming\Emby-Server\system"

# 只编某一个目标
pwsh ./build.ps1 -TargetFramework net8.0
```

或者直接用 dotnet：

```powershell
$env:EMBY_SYSTEM_DIR = "C:\Users\<你>\AppData\Roaming\Emby-Server\system"
dotnet build Emby.Plugin.OverviewTranslator.csproj -c Release
```

产物在两个目录（`BaseOutputPath` 把中间文件都留在仓库自己的 `artifacts/` 下，
不污染机器级 .NET 状态）：

```
artifacts/bin/Release/net8.0/Emby.Plugin.OverviewTranslator.dll   → Emby 4.10+
artifacts/bin/Release/net6.0/Emby.Plugin.OverviewTranslator.dll   → Emby 4.9.x
```

**找不到 Emby 程序集时**，构建会直接给出中文报错并中止，提示你用
`-p:EmbySystemDir="..."` 或环境变量 `EMBY_SYSTEM_DIR` 指定，而不是丢一堆 `CS0246` 出来。

> **给 net6.0 目标选对 Emby 目录**：如果用 Emby **4.10**（.NET 8）的 `system` 目录去编
> net6.0，会成功但出现 6 条 `MSB3277` 版本冲突警告（4.10 的程序集依赖 .NET 8 版本的
> `System.*`，而 net6.0 引用包里是 6.0 版本）。干净的做法是给 net6.0 指一个真正的 Emby 4.9
> 安装目录。用下面 CI 那套 4.9 官方程序集时，两个目标都是 **0 警告 0 错误**。

### CI

`.github/workflows/build.yml` 在每次 push / PR 上构建两个目标框架，并把两个 DLL 作为
artifact 上传（`Emby.Plugin.OverviewTranslator-net6.0` / `-net8.0`）。

CI 里没有安装 Emby，它用的是 **Emby 官方发布在 nuget.org 上的
`MediaBrowser.Server.Core` + `MediaBrowser.Common` 包**（`4.9.1.90`，`lib/netstandard2.0`），
解包出编译期需要的程序集再交给 `-p:EmbySystemDir=`。这些包是 netstandard2.0 的，
因此 **net6.0 与 net8.0 两个目标都能编译**。

需要说明两点：

- **NuGet 上并没有 4.10.1.0。** 该包在 nuget.org 上的版本止于 `4.9.1.90`（其后只有
  `4.10.0.1-beta`、`4.11.0.5-betaN` 这类 beta），所以 CI **拿不到 4.10 的正式程序集**，
  它编译出的 net8.0 产物引用的是 4.9.1.90。Emby 的程序集没有强名称，运行时按简单名解析，
  正常可以加载，但**我没有把 CI 产出的 DLL 装到 Emby 4.10 上验证过**。
  要装到 4.10，请按上面「编译」一节用你本机 Emby 4.10 的 `system` 目录自己编一份。
- CI 同时装了 6.0.x 和 8.0.x SDK。

---

## HTTP 接口

插件注册了 REST 接口（需要 Emby 的 API Key 或登录令牌）：

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/OverviewTranslator/Settings` | 读当前设置（**不回传 API Key**，只回 `HasApiKey`） |
| POST | `/OverviewTranslator/Settings` | 更新设置 |
| POST | `/OverviewTranslator/Translate/{ItemId}` | 翻译单个条目并写回 |
| POST | `/OverviewTranslator/Batch` | 启动一次批量翻译 |
| GET | `/OverviewTranslator/Status` | 待翻译数量、是否在跑、上次结果/错误 |
| POST | `/OverviewTranslator/Test` | 测试当前引擎连通性 |

用户脚本用的就是 `Translate/{ItemId}`。命令行示例：

```powershell
$key = $env:EMBY_API_KEY      # 不要把真实 Key 写进脚本或仓库
Invoke-RestMethod -Method Post -Headers @{ 'X-Emby-Token' = $key } `
  -Uri 'http://localhost:8096/OverviewTranslator/Translate/<ItemId>'
```

---

## 排错

- **日志**：`<programdata>\logs\embyserver.txt`，搜 `Overview Translator` 或 `[简介翻译]`。
- **后端连通性**：设置页上的「测试后端」按钮，或 `POST /OverviewTranslator/Test`。
- **待翻译数量**：设置页的状态区，或 `GET /OverviewTranslator/Status`。
- **插件没出现在插件列表**：多半是 Emby 版本与 DLL 的目标框架不匹配（见
  [兼容性矩阵](#兼容性矩阵)）；日志里会有加载失败的原因。
- **翻译结果是"没有变化"**：先看是不是命中了翻译记忆，再看源语言是不是 `auto` 而引擎不支持
  （MyMemory，见[已知限制](#已知限制请先读)第 9 条）。

---

## 验证记录

本仓库的源码与下列验证所用版本**逐文件一致**（30 个 `.cs` / `.html` / `.js` 文件逐字节相同，
已用 SHA-256 逐个比对），差异只在构建配置上：

- `csproj`：单目标 `net8.0` → 多目标 `net6.0;net8.0`；补充仓库/许可证元数据；
  去掉代码里**没有使用**的 `Emby.Web.GenericEdit` / `Emby.Web.GenericUI` 两个引用
  （编译期引用，去掉后两个目标都能编，且插件运行不依赖它们）；
- `Directory.Build.props`：去掉写死的 `C:\Users\...` 绝对路径，改为
  `-p:EmbySystemDir=` / `EMBY_SYSTEM_DIR` / 常见安装位置自动探测。

没有任何 `.cs` / `.html` / `.js` 逻辑改动。

| 验证 | 结果 |
|---|---|
| Release 编译 | **0 警告 0 错误**（本仓库 net6.0 与 net8.0 两个目标各自实测，见下） |
| 自建端到端测试套件 | **51 通过 / 0 失败**，另有 2 例跳过（跳过项 = 沙箱禁止 .NET 出站 TLS）；覆盖编号契约、语言判定、JSON 解析、缓存、DLL 结构 |
| 独立对抗性验证 | 四轮，累计**证伪 10 个真实缺陷**且已全部修复；最终一轮**无新增缺陷** |
| 真实 Emby 4.10.1.0 | 真实日文简介 **178 字符 → 中文 139 字符**，成功写回数据库 |
| 自动触发 | `POST /Items/{id}/Refresh` → 防抖 → 自动翻译 → 写回，全程无手动操作 |

对抗性验证抓到的真实缺陷（都已修复，列出来是因为它们能说明测试覆盖到了什么）：

| 缺陷 | 后果 |
|---|---|
| 构造函数里读 `this.Configuration` | 插件完全加载不了 |
| 批量编号解析时 fallback 编号与真实编号共用 key | 译文写到错误的剧集上 |
| 编号前缀被原样写进简介 | 数据脏 |
| `StripLeadingNumber` 砍掉合法开头数字 | 「2024年…」变成「4年…」 |
| 多条时 `1:` / `1.` 前缀不剥离 | 数据脏 |
| 位置映射门控写反，全编号时忽略编号 | 乱序编号时三条全错配 |
| no-op 检测缺失（MyMemory 回显原文） | 把原文当译文写回并计成功 |

### 本仓库的构建实测

| 目标框架 | Emby 程序集来源 | 结果 |
|---|---|---|
| net8.0 | 真实 Emby 4.10.1.0 `system` 目录 | 0 警告 0 错误 |
| net6.0 + net8.0 | 官方 NuGet `MediaBrowser.Server.Core`/`MediaBrowser.Common` 4.9.1.90 | 0 警告 0 错误 |
| net6.0 | 真实 Emby 4.10.1.0 `system` 目录（不匹配，仅作对照） | 0 错误，6 条 `MSB3277` |

---

## 许可证与贡献

- 许可证：**GPL-2.0**（见 [LICENSE](LICENSE)）。Emby 服务端本身也以 GPL-2.0 发布，
  插件跟随同一许可证。
- 欢迎 issue 与 PR。提交前请确认：
  - `dotnet build -c Release` 在两个目标上都是 0 警告 0 错误；
  - **不要提交任何密钥**（API Key、令牌、代理口令）、不要提交 Emby 的服务端程序集或
    编译产物；
  - 提交信息说清"改了什么、为什么"。
- 本插件与 Emby 官方无关；Emby 及其程序集版权归 Emby 团队所有。
