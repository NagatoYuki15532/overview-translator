using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.OverviewTranslator;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Services;
using Emby.Plugin.OverviewTranslator.Translation;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Plugin.OverviewTranslator.Api
{
    /*
     * Emby 4.10 的插件 REST 端点约定（已在本机 4.10.1.0 程序集上核实，不要凭记忆改）：
     *
     *   - 没有 MediaBrowser.Controller.Net.BaseApiController 这个类型。真实基类是
     *     MediaBrowser.Controller.Api.BaseApiService，它实现
     *     MediaBrowser.Model.Services.IService + IRequiresRequest，并自带
     *     MediaBrowser.Model.Services.IRequest 类型的 Request 属性。
     *   - 路由写在「请求 DTO」上：MediaBrowser.Model.Services.RouteAttribute(path, verbs)。
     *     路径必须是绝对路径（前导 '/'），例如 "/OverviewTranslator/Settings"。
     *   - 控制器方法名固定为 Get / Post / Delete / Any，参数是请求 DTO；
     *     返回值可以是 object / Task / Task<object>。
     *   - 认证/授权属性打在「控制器类」上（Bangumi 的 OAuthController 就是把
     *     [Unauthenticated] 打在类上）：MediaBrowser.Controller.Net.AuthenticatedAttribute。
     */

    /// <summary>GET /OverviewTranslator/Settings 的请求。无参数。</summary>
    [Route("/OverviewTranslator/Settings", "GET")]
    public class GetTranslationSettings : IReturn<TranslationSettingsResponse>
    {
    }

    /// <summary>POST /OverviewTranslator/Settings 的请求。请求体是 JSON，字段见 <see cref="TranslationSettingsRequest"/>。</summary>
    [Route("/OverviewTranslator/Settings", "POST")]
    public class UpdateTranslationSettings : TranslationSettingsRequest, IReturn<TranslationSettingsResponse>
    {
    }

    /// <summary>POST /OverviewTranslator/Translate/{ItemId} 的请求。</summary>
    [Route("/OverviewTranslator/Translate/{ItemId}", "POST")]
    public class TranslateItemOverview : IReturn<ItemTranslationOutcome>
    {
        /// <summary>要翻译的 Emby 条目 Id（由路径段绑定，必须与路由占位符同名）。</summary>
        public string ItemId { get; set; }
    }

    /// <summary>POST /OverviewTranslator/Batch 的请求。立即返回，翻译在后台进行。</summary>
    [Route("/OverviewTranslator/Batch", "POST")]
    public class StartBatchTranslation : IReturn<BatchStartResponse>
    {
    }

    /// <summary>GET /OverviewTranslator/Status 的请求。</summary>
    [Route("/OverviewTranslator/Status", "GET")]
    public class GetTranslationStatus : IReturn<TranslationStatusResponse>
    {
    }

    /// <summary>POST /OverviewTranslator/Test 的请求。测试当前后端是否可用。</summary>
    [Route("/OverviewTranslator/Test", "POST")]
    public class TestTranslationBackend : IReturn<BackendTestResponse>
    {
    }

    /*
     * ============================ 响应模型 ============================
     * 响应模型里「没有」任何 ApiKey 属性：这是 ApiKey 不以明文出网的静态保证。
     * 请求模型（*Request）才带 ApiKey。两者刻意不共用类型。
     */

    /// <summary>GET/POST /OverviewTranslator/Settings 的响应。绝不含明文密钥。</summary>
    public class TranslationSettingsResponse
    {
        /// <summary>总开关。</summary>
        public bool Enabled { get; set; }

        /// <summary>当前引擎（枚举名，例如 DeepSeek / LibreTranslate）。</summary>
        public string Backend { get; set; }

        /// <summary>下拉框可选项。前端据此渲染，避免把枚举列表复制到页面里。</summary>
        public List<BackendOption> Backends { get; set; }

        /// <summary>源语言，"auto" 表示自动。</summary>
        public string SourceLanguage { get; set; }

        /// <summary>目标语言，例如 zh-CN。</summary>
        public string TargetLanguage { get; set; }

        /// <summary>自动翻译任务间隔分钟数，0 表示不自动运行。</summary>
        public int IntervalMinutes { get; set; }

        /// <summary>新入库/元数据刷新时是否自动翻译。</summary>
        public bool AutoTranslateOnNewItems { get; set; }

        /// <summary>自动翻译的防抖延迟秒数（等元数据写入完成再翻）。</summary>
        public int AutoTranslateDelaySeconds { get; set; }

        /// <summary>是否向 Emby 网页注入「翻译简介」按钮。</summary>
        public bool EnableWebButton { get; set; }

        /// <summary>参与翻译的条目类型。</summary>
        public List<string> ItemTypes { get; set; }

        /// <summary>可选条目类型。</summary>
        public List<string> ItemTypeOptions { get; set; }

        /// <summary>LLM 后端设置（ApiKey 只回是否已设置）。</summary>
        public LlmSettingsResponse Llm { get; set; }

        /// <summary>免密钥后端设置。</summary>
        public FreeSettingsResponse Free { get; set; }

        /// <summary>网络/代理设置。</summary>
        public NetworkSettingsResponse Network { get; set; }

        /// <summary>批量与范围设置。</summary>
        public ScopeSettingsResponse Scope { get; set; }
    }

    /// <summary>一个引擎选项。Name 用于回传，Label 用于显示。</summary>
    public class BackendOption
    {
        public string Name { get; set; }

        public string Label { get; set; }

        public bool NeedsApiKey { get; set; }

        public bool NeedsBaseUrl { get; set; }
    }

    /// <summary>LLM 设置响应。注意：只有 <see cref="HasApiKey"/>，没有 ApiKey。</summary>
    public class LlmSettingsResponse
    {
        public string BaseUrl { get; set; }

        /// <summary>是否已配置 API Key。只回布尔，永不回明文。</summary>
        public bool HasApiKey { get; set; }

        public string Model { get; set; }

        public int TimeoutSeconds { get; set; }

        public int BatchSize { get; set; }

        public double Temperature { get; set; }

        public int MaxTokens { get; set; }

        public string ExtraPrompt { get; set; }
    }

    /// <summary>免密钥后端设置响应。</summary>
    public class FreeSettingsResponse
    {
        public string LibreTranslateUrl { get; set; }

        /// <summary>LibreTranslate 是否配置了 API Key（只回布尔）。</summary>
        public bool HasLibreTranslateApiKey { get; set; }

        public int TimeoutSeconds { get; set; }
    }

    /// <summary>网络设置响应。</summary>
    public class NetworkSettingsResponse
    {
        public bool UseProxy { get; set; }

        public string ProxyHost { get; set; }

        public int ProxyPort { get; set; }
    }

    /// <summary>批量与范围设置响应。</summary>
    public class ScopeSettingsResponse
    {
        public int MaxItemsPerRun { get; set; }

        public bool SkipAlreadyTranslated { get; set; }

        public int DelayBetweenItemsMs { get; set; }

        public int RetryCount { get; set; }

        public string IncludeLibraries { get; set; }
    }

    /// <summary>POST /OverviewTranslator/Batch 的响应。</summary>
    public class BatchStartResponse
    {
        /// <summary>true 表示后台任务已启动；false 表示已有任务在跑。</summary>
        public bool Started { get; set; }

        public string Message { get; set; }
    }

    /// <summary>GET /OverviewTranslator/Status 的响应。</summary>
    public class TranslationStatusResponse
    {
        /// <summary>当前符合条件、待翻译的条目数。-1 表示暂时统计不出来（见 <see cref="PendingError"/>）。</summary>
        public int Pending { get; set; }

        /// <summary>统计待翻译条目失败时的原因，正常时为 null。</summary>
        public string PendingError { get; set; }

        /// <summary>后台批量任务是否正在运行。</summary>
        public bool Running { get; set; }

        public bool Enabled { get; set; }

        /// <summary>是否在详情页显示「翻译简介」按钮。前端据此决定要不要注入按钮。</summary>
        public bool EnableWebButton { get; set; }

        /// <summary>新入库/元数据刷新时是否自动翻译。</summary>
        public bool AutoTranslateOnNewItems { get; set; }

        /// <summary>当前排队等待自动翻译的条目数。</summary>
        public int AutoTranslateQueued { get; set; }

        /// <summary>本次 Emby 运行期间自动翻译成功的累计条数。</summary>
        public long AutoTranslateDone { get; set; }

        public string Backend { get; set; }

        public DateTime? LastRunStartedUtc { get; set; }

        public DateTime? LastRunFinishedUtc { get; set; }

        /// <summary>最近一次运行的进度文案。</summary>
        public string LastRunMessage { get; set; }

        /// <summary>最近一次运行的进度百分比（0-100）。</summary>
        public double LastRunPercent { get; set; }

        /// <summary>最近一次运行的统计，未运行过时为 null。</summary>
        public BatchReport LastRunReport { get; set; }

        /// <summary>最近一次运行的错误，正常时为 null。</summary>
        public string LastError { get; set; }
    }

    /// <summary>POST /OverviewTranslator/Test 的响应。</summary>
    public class BackendTestResponse
    {
        /// <summary>true 表示后端可用。</summary>
        public bool Ok { get; set; }

        /// <summary>成功或失败的中文说明。</summary>
        public string Message { get; set; }
    }

    /*
     * ============================ 请求模型 ============================
     */

    /// <summary>POST /OverviewTranslator/Settings 的请求体。</summary>
    public class TranslationSettingsRequest
    {
        public bool Enabled { get; set; }

        /// <summary>引擎枚举名。无法识别时保留原值。</summary>
        public string Backend { get; set; }

        public string SourceLanguage { get; set; }

        public string TargetLanguage { get; set; }

        public int IntervalMinutes { get; set; }

        /// <summary>新入库/元数据刷新时自动翻译。</summary>
        public bool AutoTranslateOnNewItems { get; set; }

        /// <summary>自动翻译防抖延迟秒数。小于 5 会被服务端抬到 5。</summary>
        public int AutoTranslateDelaySeconds { get; set; }

        public bool EnableWebButton { get; set; }

        /// <summary>参与翻译的条目类型；为 null 或空时保留原值。</summary>
        public List<string> ItemTypes { get; set; }

        public LlmSettingsRequest Llm { get; set; }

        public FreeSettingsRequest Free { get; set; }

        public NetworkSettingsRequest Network { get; set; }

        public ScopeSettingsRequest Scope { get; set; }
    }

    /// <summary>LLM 设置请求。唯一的 ApiKey 入口。</summary>
    public class LlmSettingsRequest
    {
        public string BaseUrl { get; set; }

        /// <summary>新 API Key。留空表示「保持原密钥不变」。</summary>
        public string ApiKey { get; set; }

        /// <summary>true 表示清空已保存的 API Key。</summary>
        public bool ClearApiKey { get; set; }

        public string Model { get; set; }

        public int TimeoutSeconds { get; set; }

        public int BatchSize { get; set; }

        public double Temperature { get; set; }

        public int MaxTokens { get; set; }

        public string ExtraPrompt { get; set; }
    }

    /// <summary>免密钥后端设置请求。</summary>
    public class FreeSettingsRequest
    {
        public string LibreTranslateUrl { get; set; }

        /// <summary>新 LibreTranslate API Key。留空表示保持原值。</summary>
        public string LibreTranslateApiKey { get; set; }

        /// <summary>true 表示清空已保存的 LibreTranslate API Key。</summary>
        public bool ClearLibreTranslateApiKey { get; set; }

        public int TimeoutSeconds { get; set; }
    }

    /// <summary>网络设置请求。</summary>
    public class NetworkSettingsRequest
    {
        public bool UseProxy { get; set; }

        public string ProxyHost { get; set; }

        public int ProxyPort { get; set; }
    }

    /// <summary>批量与范围设置请求。</summary>
    public class ScopeSettingsRequest
    {
        public int MaxItemsPerRun { get; set; }

        public bool SkipAlreadyTranslated { get; set; }

        public int DelayBetweenItemsMs { get; set; }

        public int RetryCount { get; set; }

        public string IncludeLibraries { get; set; }
    }

    /*
     * ============================ 控制器 ============================
     */

    /// <summary>
    /// 插件对外的 REST 端点。所有翻译动作都转发给 <see cref="ITranslationService"/>，
    /// 本类不建立任何 HTTP 连接、不引用 HttpClient。
    /// 读接口只要求登录，写接口要求管理员（Emby 的配置页本身也是 admin 路由）。
    /// </summary>
    [Authenticated]
    public class TranslationController : BaseApiService
    {
        /// <summary>后台批量任务的互斥锁与进度快照。整个插件只允许一个批量任务同时运行。</summary>
        private static readonly object BatchLock = new object();

        private static bool _batchRunning;

        private static BatchProgress _batchProgress;

        private readonly ISessionContext _sessionContext;

        private readonly ILogger _logger;

        /// <summary>
        /// 构造注入只使用 Emby 自带的服务（<see cref="ISessionContext"/>、<see cref="ILogger"/>）。
        /// 插件自己的 <see cref="ITranslationService"/> 不注册进容器（Emby 4.10 没有稳定的
        /// RegisterService 契约），统一从静态宿主 <see cref="TranslationServiceHost"/> 取。
        /// </summary>
        public TranslationController(ISessionContext sessionContext, ILogger logger)
        {
            this._sessionContext = sessionContext;
            this._logger = logger;
        }

        /// <summary>读取当前配置（密钥只回是否已设置）。需要管理员。</summary>
        public object Get(GetTranslationSettings request)
        {
            this.RequireAdministrator();
            return BuildSettingsResponse();
        }

        /// <summary>保存配置。需要管理员。</summary>
        public object Post(UpdateTranslationSettings request)
        {
            this.RequireAdministrator();

            if (request == null)
            {
                throw new ArgumentException("请求体为空，无法保存设置。");
            }

            this.ApplySettings(request);
            return BuildSettingsResponse();
        }

        /// <summary>翻译单个条目并写回 Emby。需要管理员。</summary>
        public async Task<object> Post(TranslateItemOverview request)
        {
            this.RequireAdministrator();

            string itemId = request == null ? null : request.ItemId;
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentException("缺少要翻译的条目 Id。");
            }

            // force: true —— 详情页按钮是显式的人工操作，应当绕过「已翻译」与缓存判定。
            ITranslationService service = this.RequireService();

            ItemTranslationOutcome outcome = await service
                .TranslateAndSaveItemAsync(itemId.Trim(), true, CancellationToken.None)
                .ConfigureAwait(false);

            return outcome;
        }

        /// <summary>启动后台批量翻译，立即返回，不阻塞请求线程。需要管理员。</summary>
        public object Post(StartBatchTranslation request)
        {
            this.RequireAdministrator();

            // 先确认服务可用：否则会把 "运行中" 标志置位后立刻失败，永久卡住批量入口。
            ITranslationService service = this.RequireService();

            lock (BatchLock)
            {
                if (_batchRunning)
                {
                    return new BatchStartResponse
                    {
                        Started = false,
                        Message = "已有一个批量翻译任务正在运行，请等待它结束。"
                    };
                }

                _batchRunning = true;
                _batchProgress = new BatchProgress
                {
                    Running = true,
                    StartedUtc = DateTime.UtcNow,
                    Message = "正在准备批量翻译…",
                    Percent = 0
                };
            }

            // 请求线程只负责启动：进度写入静态快照，由 GET /Status 读取。
            ILogger logger = this._logger;

            Task.Run(
                async () =>
                {
                    try
                    {
                        BatchReport report = await service
                            .TranslateLibraryAsync(UpdateBatchProgress, CancellationToken.None)
                            .ConfigureAwait(false);

                        lock (BatchLock)
                        {
                            if (_batchProgress != null)
                            {
                                _batchProgress.Running = false;
                                _batchProgress.FinishedUtc = DateTime.UtcNow;
                                _batchProgress.Percent = 100;
                                _batchProgress.Message = string.Format(
                                    "批量翻译结束：检查 {0}，翻译 {1}，写回 {2}，跳过 {3}，失败 {4}。",
                                    report.Examined,
                                    report.Translated,
                                    report.Saved,
                                    report.Skipped,
                                    report.Failed);
                                _batchProgress.Report = report;
                            }
                        }

                        logger.Info("OverviewTranslator 批量翻译完成：{0}", _batchProgress == null ? "无" : _batchProgress.Message);
                    }
                    catch (Exception ex)
                    {
                        logger.ErrorException("OverviewTranslator 批量翻译失败。", ex);

                        lock (BatchLock)
                        {
                            if (_batchProgress != null)
                            {
                                _batchProgress.Running = false;
                                _batchProgress.FinishedUtc = DateTime.UtcNow;
                                _batchProgress.Message = "批量翻译失败：" + ex.Message;
                                _batchProgress.Error = ex.Message;
                            }
                        }
                    }
                    finally
                    {
                        lock (BatchLock)
                        {
                            _batchRunning = false;
                            if (_batchProgress != null)
                            {
                                _batchProgress.Running = false;
                            }
                        }
                    }
                });

            return new BatchStartResponse
            {
                Started = true,
                Message = "批量翻译已在后台启动，可稍后刷新查看状态。"
            };
        }

        /// <summary>返回待翻译数量与最近一次批量运行信息。只要求登录。</summary>
        public async Task<object> Get(GetTranslationStatus request)
        {
            // 该接口不含任何机密，普通用户也可读，便于详情页按钮提示进度。
            this.RequireUser();

            int pending;
            string pendingError = null;
            try
            {
                pending = await this.RequireService().CountPendingAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 统计失败（含服务尚未初始化）不应让状态接口整页报错，退化成一个可读的错误文案。
                this._logger.ErrorException("OverviewTranslator 统计待翻译条目失败。", ex);
                pending = -1;
                pendingError = ex.Message;
            }

            PluginConfiguration config = Plugin.CurrentConfiguration;

            var autoTranslate = ServerEntryPoint.AutoTranslate;

            var response = new TranslationStatusResponse
            {
                Pending = pending,
                PendingError = pendingError,
                Enabled = config.Enabled,
                EnableWebButton = config.EnableWebButton,
                AutoTranslateOnNewItems = config.AutoTranslateOnNewItems,
                AutoTranslateQueued = autoTranslate == null ? 0 : autoTranslate.PendingCount,
                AutoTranslateDone = autoTranslate == null ? 0 : autoTranslate.TranslatedTotal,
                Backend = config.Backend.ToString()
            };

            lock (BatchLock)
            {
                if (_batchProgress != null)
                {
                    response.Running = _batchProgress.Running;
                    response.LastRunStartedUtc = _batchProgress.StartedUtc;
                    response.LastRunFinishedUtc = _batchProgress.FinishedUtc;
                    response.LastRunMessage = _batchProgress.Message;
                    response.LastRunPercent = _batchProgress.Percent;
                    response.LastRunReport = _batchProgress.Report;
                    response.LastError = _batchProgress.Error;
                }
            }

            return response;
        }

        /// <summary>测试当前后端是否可用。需要管理员。</summary>
        public async Task<object> Post(TestTranslationBackend request)
        {
            this.RequireAdministrator();

            string error = null;
            try
            {
                error = await this.RequireService().TestBackendAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (BackendConfigurationException ex)
            {
                // 配置不可用是可预期的用户错误，不需要堆栈。
                error = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                // 服务尚未初始化：转成可读文案，而不是 500 堆栈。
                this._logger.ErrorException("OverviewTranslator 后端测试时服务尚不可用。", ex);
                error = ex.Message;
            }
            catch (Exception ex)
            {
                this._logger.ErrorException("OverviewTranslator 后端测试失败。", ex);
                error = ex.Message;
            }

            return new BackendTestResponse
            {
                Ok = string.IsNullOrWhiteSpace(error),
                Message = string.IsNullOrWhiteSpace(error) ? "后端连通性测试通过。" : error
            };
        }

        /// <summary>把批量任务的进度写进静态快照，供 /Status 读取。</summary>
        private static void UpdateBatchProgress(string message, double percent)
        {
            lock (BatchLock)
            {
                if (_batchProgress == null)
                {
                    return;
                }

                _batchProgress.Message = message;
                _batchProgress.Percent = percent;
            }
        }

        /// <summary>要求「已登录且是管理员」，否则抛 403。</summary>
        private User RequireAdministrator()
        {
            User user = this.RequireUser();
            if (user.Policy == null || !user.Policy.IsAdministrator)
            {
                throw new SecurityException("只有 Emby 管理员可以修改简介翻译设置。", "OverviewTranslator");
            }

            return user;
        }

        /// <summary>要求「已登录」，返回当前用户。</summary>
        private User RequireUser()
        {
            User user = this.Request == null ? null : this._sessionContext.GetUser(this.Request);
            if (user == null)
            {
                throw new SecurityException("使用简介翻译功能需要先登录 Emby。", "OverviewTranslator");
            }

            return user;
        }

        /// <summary>
        /// 取插件翻译服务。极早期请求可能撞上 Emby 还没把宿主初始化完，
        /// <see cref="TranslationServiceHost.Service"/> 会抛带中文说明的
        /// <see cref="InvalidOperationException"/>，这里补一条日志后原样抛出。
        /// </summary>
        private ITranslationService RequireService()
        {
            try
            {
                return TranslationServiceHost.Service;
            }
            catch (InvalidOperationException ex)
            {
                this._logger.ErrorException("OverviewTranslator 翻译服务尚不可用。", ex);
                throw;
            }
        }

        /// <summary>把经过校验的请求写进插件配置并落盘。</summary>
        private void ApplySettings(TranslationSettingsRequest request)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null)
            {
                throw new InvalidOperationException("插件尚未被 Emby 加载，无法保存设置。");
            }

            // 原地修改 Plugin.CurrentConfiguration 指向的对象：这样服务端已经持有的引用也会立即看到新值。
            PluginConfiguration config = Plugin.CurrentConfiguration;

            config.Enabled = request.Enabled;
            config.EnableWebButton = request.EnableWebButton;
            config.IntervalMinutes = Clamp(request.IntervalMinutes, 0, 10080);
            config.AutoTranslateOnNewItems = request.AutoTranslateOnNewItems;

            // Below 5s the debounce would fire before metadata providers finish writing the
            // overview, which is the very race the delay exists to avoid. Upper bound is a day.
            // A missing/zero value means "leave it alone": 0 has no meaning here, and a client that
            // predates this field (or an open settings page holding older values) must not be able
            // to reset a working delay to something too short.
            if (request.AutoTranslateDelaySeconds > 0)
            {
                config.AutoTranslateDelaySeconds = Clamp(request.AutoTranslateDelaySeconds, 5, 86400);
            }

            if (!string.IsNullOrWhiteSpace(request.SourceLanguage))
            {
                config.SourceLanguage = request.SourceLanguage.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.TargetLanguage))
            {
                config.TargetLanguage = request.TargetLanguage.Trim();
            }

            TranslationBackend backend;
            if (!string.IsNullOrWhiteSpace(request.Backend)
                && Enum.TryParse(request.Backend.Trim(), true, out backend)
                && Enum.IsDefined(typeof(TranslationBackend), backend))
            {
                config.Backend = backend;
            }

            if (request.ItemTypes != null && request.ItemTypes.Count > 0)
            {
                // Distinct: a client that serialises its list awkwardly can send the same type
                // several times, and without this the duplicate accumulates in the config file on
                // every save (observed as "Episode,Episode,Episode").
                List<string> types = request.ItemTypes
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .Select(i => i.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList();

                if (types.Count > 0)
                {
                    config.ItemTypes = types;
                }
            }

            config.Normalize();

            if (request.Llm != null)
            {
                LlmSettings llm = config.Llm;

                if (request.Llm.BaseUrl != null)
                {
                    llm.BaseUrl = request.Llm.BaseUrl.Trim();
                }

                if (request.Llm.Model != null)
                {
                    llm.Model = request.Llm.Model.Trim();
                }

                if (request.Llm.ExtraPrompt != null)
                {
                    llm.ExtraPrompt = request.Llm.ExtraPrompt;
                }

                // 密钥三态：清空 > 覆盖 > 保持。空字符串一律解释为「保持」，避免把密钥写成空。
                if (request.Llm.ClearApiKey)
                {
                    llm.ApiKey = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(request.Llm.ApiKey))
                {
                    llm.ApiKey = request.Llm.ApiKey.Trim();
                }

                if (request.Llm.TimeoutSeconds > 0)
                {
                    llm.TimeoutSeconds = Clamp(request.Llm.TimeoutSeconds, 5, 600);
                }

                if (request.Llm.BatchSize > 0)
                {
                    llm.BatchSize = Clamp(request.Llm.BatchSize, 1, 50);
                }

                if (request.Llm.Temperature > 0)
                {
                    llm.Temperature = Math.Min(2.0, request.Llm.Temperature);
                }

                if (request.Llm.MaxTokens > 0)
                {
                    llm.MaxTokens = Clamp(request.Llm.MaxTokens, 64, 32000);
                }
            }

            if (request.Free != null)
            {
                FreeBackendSettings free = config.Free;

                if (request.Free.LibreTranslateUrl != null)
                {
                    free.LibreTranslateUrl = request.Free.LibreTranslateUrl.Trim();
                }

                if (request.Free.ClearLibreTranslateApiKey)
                {
                    free.LibreTranslateApiKey = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(request.Free.LibreTranslateApiKey))
                {
                    free.LibreTranslateApiKey = request.Free.LibreTranslateApiKey.Trim();
                }

                if (request.Free.TimeoutSeconds > 0)
                {
                    free.TimeoutSeconds = Clamp(request.Free.TimeoutSeconds, 5, 600);
                }
            }

            if (request.Network != null)
            {
                NetworkSettings network = config.Network;

                network.UseProxy = request.Network.UseProxy;

                if (request.Network.ProxyHost != null)
                {
                    network.ProxyHost = request.Network.ProxyHost.Trim();
                }

                if (request.Network.ProxyPort > 0)
                {
                    network.ProxyPort = Clamp(request.Network.ProxyPort, 1, 65535);
                }
            }

            if (request.Scope != null)
            {
                ScopeSettings scope = config.Scope;

                if (request.Scope.MaxItemsPerRun >= 0)
                {
                    scope.MaxItemsPerRun = Clamp(request.Scope.MaxItemsPerRun, 0, 1000000);
                }

                scope.SkipAlreadyTranslated = request.Scope.SkipAlreadyTranslated;

                if (request.Scope.DelayBetweenItemsMs >= 0)
                {
                    scope.DelayBetweenItemsMs = Clamp(request.Scope.DelayBetweenItemsMs, 0, 60000);
                }

                if (request.Scope.RetryCount >= 0)
                {
                    scope.RetryCount = Clamp(request.Scope.RetryCount, 0, 10);
                }

                if (request.Scope.IncludeLibraries != null)
                {
                    scope.IncludeLibraries = request.Scope.IncludeLibraries.Trim();
                }
            }

            // UpdateConfiguration 会替换配置实例并落盘；再显式调用 SaveConfiguration 保证持久化。
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();

            this._logger.Info("OverviewTranslator 设置已保存（引擎 {0}）。", config.Backend.ToString());
        }

        /// <summary>按响应模型（无任何密钥字段）拼装当前设置。</summary>
        private static TranslationSettingsResponse BuildSettingsResponse()
        {
            PluginConfiguration config = Plugin.CurrentConfiguration;

            return new TranslationSettingsResponse
            {
                Enabled = config.Enabled,
                Backend = config.Backend.ToString(),
                Backends = BuildBackendOptions(),
                SourceLanguage = config.SourceLanguage,
                TargetLanguage = config.TargetLanguage,
                IntervalMinutes = config.IntervalMinutes,
                AutoTranslateOnNewItems = config.AutoTranslateOnNewItems,
                AutoTranslateDelaySeconds = config.AutoTranslateDelaySeconds,
                EnableWebButton = config.EnableWebButton,
                ItemTypes = new List<string>(config.ItemTypes),
                ItemTypeOptions = new List<string> { "Episode", "Movie", "Series", "Season", "Video", "MusicVideo", "Trailer" },
                Llm = new LlmSettingsResponse
                {
                    BaseUrl = config.Llm.BaseUrl,
                    // 只回布尔：明文密钥永不出网。
                    HasApiKey = !string.IsNullOrWhiteSpace(config.Llm.ApiKey),
                    Model = config.Llm.Model,
                    TimeoutSeconds = config.Llm.TimeoutSeconds,
                    BatchSize = config.Llm.BatchSize,
                    Temperature = config.Llm.Temperature,
                    MaxTokens = config.Llm.MaxTokens,
                    ExtraPrompt = config.Llm.ExtraPrompt
                },
                Free = new FreeSettingsResponse
                {
                    LibreTranslateUrl = config.Free.LibreTranslateUrl,
                    HasLibreTranslateApiKey = !string.IsNullOrWhiteSpace(config.Free.LibreTranslateApiKey),
                    TimeoutSeconds = config.Free.TimeoutSeconds
                },
                Network = new NetworkSettingsResponse
                {
                    UseProxy = config.Network.UseProxy,
                    ProxyHost = config.Network.ProxyHost,
                    ProxyPort = config.Network.ProxyPort
                },
                Scope = new ScopeSettingsResponse
                {
                    MaxItemsPerRun = config.Scope.MaxItemsPerRun,
                    SkipAlreadyTranslated = config.Scope.SkipAlreadyTranslated,
                    DelayBetweenItemsMs = config.Scope.DelayBetweenItemsMs,
                    RetryCount = config.Scope.RetryCount,
                    IncludeLibraries = config.Scope.IncludeLibraries
                }
            };
        }

        /// <summary>固定顺序的引擎列表，前端直接用，避免页面与枚举不同步。</summary>
        private static List<BackendOption> BuildBackendOptions()
        {
            return new List<BackendOption>
            {
                new BackendOption { Name = "DeepSeek", Label = "DeepSeek（官方 API）", NeedsApiKey = true, NeedsBaseUrl = true },
                new BackendOption { Name = "OpenAiCompatible", Label = "OpenAI 兼容（OpenAI / 智谱 / Qwen / Ollama / one-api）", NeedsApiKey = true, NeedsBaseUrl = true },
                new BackendOption { Name = "GoogleFree", Label = "Google 免密钥", NeedsApiKey = false, NeedsBaseUrl = false },
                new BackendOption { Name = "BingFree", Label = "Bing 免密钥", NeedsApiKey = false, NeedsBaseUrl = false },
                new BackendOption { Name = "MyMemoryFree", Label = "MyMemory 免密钥", NeedsApiKey = false, NeedsBaseUrl = false },
                new BackendOption { Name = "LibreTranslate", Label = "LibreTranslate（自建）", NeedsApiKey = false, NeedsBaseUrl = false }
            };
        }

        /// <summary>把数值夹到闭区间，避免前端写出让后端崩掉的值。</summary>
        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        /// <summary>后台批量任务的进度快照（静态，供 /Status 读取）。</summary>
        private sealed class BatchProgress
        {
            public bool Running { get; set; }

            public DateTime StartedUtc { get; set; }

            public DateTime? FinishedUtc { get; set; }

            public string Message { get; set; }

            public double Percent { get; set; }

            public string Error { get; set; }

            public BatchReport Report { get; set; }
        }
    }
}
