# Emby 单集简介翻译插件

把 Emby 里**还不是中文的剧集简介**自动翻译成中文，写回 Emby 数据库。

只改**「简介」这一个字段**——不动标题、不碰媒体文件、不写 NFO。

---

## 为什么要用它

如果你用 **Bangumi** 刮削日本动画，大概见过这个情况：

> 标题是中文（药屋少女的呢喃），**简介却还是日文**。

这不是设置没调对。Bangumi 的接口本身**没有语言参数**，返回什么就是什么；
Emby 的「首选元数据语言」也不会触发翻译——它只是让提供器去要**它本来就有的**对应语言文本。
Bangumi 的 `summary` 字段大多就是日文，插件只能原样搬运。

所以需要一个**事后翻译**的环节，这就是本插件做的事。

---

## 安装

### 1. 下载

去本仓库的 **[Releases](../../releases)** 页面下载 DLL。按你的 Emby 版本选：

| 你的 Emby 版本 | 选这个文件 |
|---|---|
| **4.10 及以上** | `Emby.Plugin.OverviewTranslator.net8.0.dll` |
| **4.9 及更早** | `Emby.Plugin.OverviewTranslator.net6.0.dll` |

> 怎么看 Emby 版本：控制台右下角，或浏览器访问 `http://你的地址:8096/emby/System/Info/Public`。

### 2. 放进插件目录

把下载到的 DLL **重命名**为 `Emby.Plugin.OverviewTranslator.dll`，放到：

- **Windows**：`%APPDATA%\Emby-Server\programdata\plugins\`
- **Linux**：`/var/lib/emby/plugins/`

### 3. 重启 Emby

重启后，控制台左侧菜单会出现 **「简介翻译」**。

### 4. 配置

打开「简介翻译」，至少填两项：

1. **翻译引擎**：建议先用 **Google 免密钥** 试通（不用填任何 Key）
2. **DeepSeek API Key**：想批量翻、质量好，用 DeepSeek（[申请地址](https://platform.deepseek.com)）

填完点 **「测试后端」**，显示通过就能用了。

> 国内访问 Google/DeepSeek 需要代理的话，把「使用代理」勾上并填好地址端口。

### 5. 开始翻译

**补存量**：控制台 → **计划任务** → 「翻译剧集简介」→ **立即运行**。

**以后自动**：设置页勾选「新入库 / 元数据刷新时自动翻译」，延迟保持默认 5 分钟。
之后新番入库或刷新元数据会自动翻译，不用你管。

---

## 翻译引擎怎么选

| 引擎 | 要 Key 吗 | 说明 |
|---|---|---|
| **DeepSeek** | 要 | **推荐**。便宜、中文质量好，按量计费 |
| OpenAI 兼容 | 要 | 可接 OpenAI / 智谱 / 通义 / 本地 Ollama / one-api |
| Google 免密钥 | 不要 | 试通链路用。**会限流（429）**，别用来批量 |
| MyMemory | 不要 | 源语言必须填 `ja`（它不支持 auto），有每日额度 |
| Bing 免密钥 | 不要 | 网页抓取实现，微软改版就会失效 |
| LibreTranslate | 不要 | 需要自己搭一个服务 |

---

## 常见问题

**Q：点「立即运行」后日志说成功，但简介没变？**
先看设置页的「源语言」。用 MyMemory 时必须填 `ja`：填 `auto` 它会拿英文去翻日文、
把日文原样返回——插件会识别这种情况并报错，不会静默写坏数据。

**Q：改了「自动翻译间隔」没生效？**
Emby 的触发器只在启动时注册，改完**重启一次 Emby**。

**Q：详情页怎么没有翻译按钮？**
Emby 不支持插件往网页里注入脚本，所以插件自己加不了按钮。
想要按钮可以装仓库里 `userscript/` 那个浏览器脚本（需要 Tampermonkey）。
**日常用「新入库自动翻译」就够了。**

**Q：会改我的媒体文件或 NFO 吗？**
不会。只调用 Emby 自己的条目更新接口改 `Overview` 字段。

**Q：翻译过的会重复花钱吗？**
不会。插件有本地翻译记忆（`cache.json`），同一段原文只翻一次。

**Q：怎么彻底卸载？**
删掉 `plugins/` 里的那个 DLL，重启 Emby。
`plugins/configurations/` 下的 `Emby.Plugin.OverviewTranslator.xml` 和 `overviewtranslator/` 目录
是设置和翻译缓存，可一并删除。

---

## 已知限制

- **只翻译单集（Episode）简介**，不处理剧集/季/电影。
- **详情页按钮需要浏览器脚本**（Emby 没有插件注入点）。
- **改「间隔分钟」需要重启 Emby** 才生效。
- **非中文目标语言不做「已翻译」判定**：翻成英文/日文等时会每条都翻（命中缓存仍不重复收费）。
- **Bing 免密钥是网页抓取**，微软改版可能失效；失效时报可读错误，不会崩。
- 自动翻译的**延迟别设太短**（最小 5 秒，默认 5 分钟）：Emby 的事件在元数据写完之前就触发了。

---

## 自己编译（可选）

不编译也能用，去 Releases 下载即可。想自己编：

```powershell
git clone https://github.com/NagatoYuki15532/overview-translator.git
cd overview-translator
.\build.ps1                      # 自动探测 Emby 安装目录
# 或显式指定：
.\build.ps1 -EmbySystemDir "C:\Users\你\AppData\Roaming\Emby-Server\system"
```

产物在 `artifacts/bin/Release/<框架>/`。

编译要求这台机器上**已经装着 Emby**——插件是编译期引用 Emby 的服务端程序集，不是 NuGet 包。

---

## 许可证

[GPL-2.0](LICENSE)。可自由使用、修改、再分发；再分发时请保持同样的开源许可。
