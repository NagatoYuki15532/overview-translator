/*
 * Overview Translator —— Emby Web 前端脚本。
 *
 * 本文件被注册为第二个 PluginPageInfo（Name = "overviewtranslator.js"），因此它同时承担两件事，
 * 两个分支互不干扰、各自兜底：
 *
 *   分支 A（普通 <script> 注入时生效）：在条目详情页注入「翻译简介」按钮。
 *   分支 B（AMD require 加载时生效）：作为设置页 configPage.html 的 data-controller 目标，
 *          提供设置页的 View（Emby 的 viewmanager.js 会读 data-controller 再 require([url])）。
 *
 * 为什么分支 B 要判断「设置页容器是否已在 DOM 中」才调用 define：
 *   Emby 4.10 的 AMD 加载器是 alameda（dashboard-ui/modules/alameda/alameda.js）。alameda 的
 *   匿名 define 会先进入内部 queue，由下一次脚本加载的 takeQueue(id) 认领。如果本脚本被服务端
 *   当作普通 <script> 注入到每个页面、又无条件调用匿名 define，就会在 queue 里留下一个
 *   「无主」模块，可能被后续任意模块加载错误认领，从而弄坏 Emby 自己的页面。
 *   因此只有在设置页容器存在（= 这次加载确实是为设置页服务的）时才注册模块，并用一次性
 *   全局标记避免重复注册。
 *
 * 服务端自动注入本脚本尚未验证；部署后由 lead 用 GET /web/index.html 实测确认。
 */

(function () {
    'use strict';

    // =====================================================================
    // 共享工具
    // =====================================================================

    var BUTTON_ID = 'overviewTranslatorTranslateButton';
    var POLL_INTERVAL_MS = 800;

    /** Emby Web 的 ApiClient 全局（模块里也能拿到）。拿不到时退回父窗口，再不行返回 null。 */
    function getApiClient() {
        if (window.ApiClient) {
            return window.ApiClient;
        }

        try {
            if (window.parent && window.parent !== window && window.parent.ApiClient) {
                return window.parent.ApiClient;
            }
        } catch (e) {
            // 跨域 iframe 会抛错，忽略。
        }

        return null;
    }

    /** 把 ApiClient/服务端返回的错误整理成一句中文。 */
    function describeError(err) {
        if (!err) {
            return '未知错误。';
        }

        if (typeof err === 'string') {
            return err;
        }

        if (err.status === 403) {
            return '权限不足：该操作需要 Emby 管理员账号。';
        }

        if (err.status === 404) {
            return '接口不存在（404），请确认插件已正确部署且 Emby 已重启。';
        }

        if (err.status === 401) {
            return '未登录或登录已过期，请重新登录 Emby。';
        }

        var text = err.message || err.statusText || '';
        if (!text && err.responseText) {
            text = String(err.responseText).substring(0, 300);
        }

        if (!text && err.status) {
            text = 'HTTP ' + err.status;
        }

        return text || '未知错误。';
    }

    /** 尽量用 Emby 的 toast；不可用时退回 alert，保证一定有可见反馈。 */
    function notify(message, isError) {
        var text = String(message == null ? '' : message);

        var alertFallback = function () {
            try {
                window.alert(text);
            } catch (e) {
                // 真的没有 UI 可用时只能放弃。
            }
        };

        try {
            if (typeof require === 'function') {
                require(['toast'], function (toast) {
                    try {
                        toast({ title: '简介翻译', message: text });
                    } catch (e) {
                        alertFallback();
                    }
                });
                return;
            }
        } catch (e) {
            // 继续走 alert。
        }

        if (isError && window.Dashboard && window.Dashboard.alert) {
            try {
                window.Dashboard.alert({ title: '简介翻译', message: text });
                return;
            } catch (e) {
                // 继续走 alert。
            }
        }

        alertFallback();
    }

    function consoleError(message, err) {
        try {
            if (window.console && window.console.error) {
                window.console.error('OverviewTranslator: ' + message, err || '');
            }
        } catch (e) {
            // 忽略。
        }
    }

    // =====================================================================
    // 分支 A：条目详情页的「翻译简介」按钮
    // =====================================================================

    (function initDetailButton() {
        try {
            var currentItemId = null;
            var buttonEnabled = true;

            /**
             * itemId -> true/false, whether that item is an Episode.
             *
             * The plugin only translates episodes, but Emby serves series, seasons, movies and
             * episodes all through the same "item" route. Injecting on the route alone put a
             * useless button on series pages (reported by the user), so the item's real type is
             * fetched once and cached.
             */
            var episodeCache = {};
            var episodeChecks = {};
            var configChecked = false;
            var configChecking = false;

            /** 解析 location.hash 里的查询串，例如 #!/item?id=123&serverId=x */
            function parseHashQuery() {
                var hash = window.location.hash || '';
                var q = hash.indexOf('?');
                var result = {};

                if (q < 0) {
                    return result;
                }

                var pairs = hash.substring(q + 1).split('&');
                for (var i = 0; i < pairs.length; i++) {
                    if (!pairs[i]) {
                        continue;
                    }

                    var eq = pairs[i].indexOf('=');
                    var key = eq < 0 ? pairs[i] : pairs[i].substring(0, eq);
                    var value = eq < 0 ? '' : pairs[i].substring(eq + 1);
                    try {
                        result[decodeURIComponent(key)] = decodeURIComponent(value.replace(/\+/g, ' '));
                    } catch (e) {
                        result[key] = value;
                    }
                }

                return result;
            }

            /** 当前 SPA 路由名，例如 item / details / home。 */
            function getRouteName() {
                var hash = (window.location.hash || '').replace(/^#!?/, '');
                var q = hash.indexOf('?');
                var route = q < 0 ? hash : hash.substring(0, q);
                return route.replace(/^\/+/, '').toLowerCase();
            }

            /**
             * 只在「条目详情页」返回 Id。
             * Emby Web 的详情页路由是 #!/item?id=xxx（旧版为 #!/details?id=xxx）；
             * 其它页面统一返回 null，从而安全退出，不做任何注入。
             */
            function getDetailItemId() {
                var route = getRouteName();
                if (route !== 'item' && route !== 'details' && route !== 'video') {
                    return null;
                }

                var query = parseHashQuery();
                var id = query.id || query.Id || query.itemId;
                return id && String(id).trim() ? String(id).trim() : null;
            }

            /** 找详情页可以挂按钮的容器；找不到时返回 null（改用右下角浮层）。 */
            function findAnchor() {
                var selectors = [
                    '.detailButtons',
                    '.mainDetailButtons',
                    '.detailPagePrimaryContainer .detailButtons',
                    '.itemDetailPage .detailButtons',
                    '.detailPageWrapperContainer .detailButtons',
                    '.detailPageContent .detailButtons'
                ];

                for (var i = 0; i < selectors.length; i++) {
                    var found = document.querySelector(selectors[i]);
                    if (found) {
                        return found;
                    }
                }

                return null;
            }

            function removeButton() {
                currentItemId = null;
                var existing = document.getElementById(BUTTON_ID);
                if (existing && existing.parentNode) {
                    existing.parentNode.removeChild(existing);
                }
            }

            /** 点击后的实际请求：POST /OverviewTranslator/Translate/{itemId}。 */
            function runTranslation(itemId, button) {
                var api = getApiClient();
                if (!api) {
                    notify('无法访问 Emby API：当前会话已失效，请刷新页面后重试。', true);
                    return;
                }

                var label = button.querySelector('.ot-translate-label');
                button.disabled = true;
                if (label) {
                    label.textContent = '翻译中…';
                }

                var restore = function () {
                    button.disabled = false;
                    if (label) {
                        label.textContent = '翻译简介';
                    }
                };

                var done = function (outcome) {
                    restore();

                    if (!outcome) {
                        notify('翻译完成，但服务端没有返回结果。', false);
                        return;
                    }

                    if (outcome.Skipped) {
                        notify('已跳过：' + (outcome.Reason || '该条目不满足翻译条件。'), false);
                        return;
                    }

                    if (outcome.Saved) {
                        notify('翻译完成，简介已更新。' + (outcome.Reason ? '（' + outcome.Reason + '）' : ''), false);
                        return;
                    }

                    if (outcome.Translated) {
                        notify('已生成译文，但内容与原文相同，未写回。', false);
                        return;
                    }

                    notify('未产生译文：' + (outcome.Reason || '原因未知。'), true);
                };

                var failed = function (err) {
                    restore();
                    notify('翻译失败：' + describeError(err), true);
                };

                try {
                    api.ajax({
                        type: 'POST',
                        url: api.getUrl('OverviewTranslator/Translate/' + encodeURIComponent(itemId)),
                        dataType: 'json'
                    }).then(done, failed);
                } catch (e) {
                    failed(e);
                }
            }

            /**
             * 判断条目是不是「单集」。插件只翻单集，所以剧集/季/影片页面不应该出现按钮。
             * 结果按 itemId 缓存；查询失败时返回 null（本次不注入，下次轮询再试）。
             */
            function isEpisode(itemId, callback) {
                if (Object.prototype.hasOwnProperty.call(episodeCache, itemId)) {
                    callback(episodeCache[itemId]);
                    return;
                }

                if (episodeChecks[itemId]) {
                    // 已经有一个同样的查询在飞，等它回来即可（避免轮询期间重复请求）。
                    return;
                }

                var api = getApiClient();
                if (!api || typeof api.getJSON !== 'function') {
                    callback(null);
                    return;
                }

                episodeChecks[itemId] = true;

                var url = api.getUrl('Items/' + encodeURIComponent(itemId) + '?Fields=Type,MediaType');
                api.getJSON(url).then(
                    function (item) {
                        delete episodeChecks[itemId];
                        var type = item && (item.Type || item.MediaType);
                        // 类型缺失时保守判断为「不是单集」，避免在剧集页误注入。
                        var isEp = String(type || '').toLowerCase() === 'episode';
                        episodeCache[itemId] = isEp;
                        callback(isEp);
                    },
                    function (err) {
                        delete episodeChecks[itemId];
                        consoleError('查询条目类型失败，暂不显示按钮。', err);
                        callback(null);
                    });
            }

            function createButton(itemId) {
                var button = document.createElement('button');
                button.id = BUTTON_ID;
                button.type = 'button';
                button.className = 'raised emby-button';
                button.setAttribute('data-ot-item-id', itemId);
                button.innerHTML = '<span class="ot-translate-label">翻译简介</span>';

                // 内联样式兜底：即使 Emby 主题类名变化，按钮依然可点可见。
                button.style.cssText = 'margin:0 8px;padding:8px 14px;border-radius:4px;'
                    + 'border:1px solid rgba(128,128,128,.5);background:rgba(128,128,128,.18);'
                    + 'color:inherit;cursor:pointer;font:inherit;';

                button.addEventListener('click', function (e) {
                    e.preventDefault();
                    runTranslation(itemId, button);
                });

                var anchor = findAnchor();
                if (anchor) {
                    anchor.appendChild(button);
                } else {
                    // 详情页结构变化时的退路：右下角浮动按钮。
                    button.style.position = 'fixed';
                    button.style.right = '18px';
                    button.style.bottom = '18px';
                    button.style.zIndex = '9999';
                    document.body.appendChild(button);
                }

                currentItemId = itemId;
            }

            /** 幂等注入：同一个条目只会存在一个按钮，切换条目或离开详情页会清理。 */
            function ensureButton() {
                var itemId = getDetailItemId();

                if (!itemId) {
                    removeButton();
                    return;
                }

                var existing = document.getElementById(BUTTON_ID);
                if (existing && existing.getAttribute('data-ot-item-id') === itemId) {
                    return;
                }

                if (existing && existing.parentNode) {
                    existing.parentNode.removeChild(existing);
                }

                currentItemId = null;
                createButton(itemId);
            }

            /**
             * 读一次插件状态，尊重「在详情页显示按钮」开关。
             * 读不到时保守放行（按钮可用，权限最终由服务端裁决）。
             */
            function refreshEnabledFlag(callback) {
                if (configChecked) {
                    callback(buttonEnabled);
                    return;
                }

                if (configChecking) {
                    return;
                }

                configChecking = true;
                var api = getApiClient();
                if (!api) {
                    configChecking = false;
                    configChecked = true;
                    buttonEnabled = false;
                    callback(false);
                    return;
                }

                api.getJSON(api.getUrl('OverviewTranslator/Status')).then(
                    function (status) {
                        configChecking = false;
                        configChecked = true;
                        buttonEnabled = !status || status.EnableWebButton !== false;
                        callback(buttonEnabled);
                    },
                    function () {
                        configChecking = false;
                        configChecked = true;
                        buttonEnabled = true;
                        callback(true);
                    });
            }

            function tick() {
                if (document.readyState === 'loading' || !document.body) {
                    return;
                }

                refreshEnabledFlag(function (enabled) {
                    if (!enabled) {
                        removeButton();
                        return;
                    }

                    // 只有单集才注入。剧集/季/影片页面直接清掉按钮（含之前误注入的浮动按钮）。
                    var itemId = getDetailItemId();
                    if (!itemId) {
                        removeButton();
                        return;
                    }

                    isEpisode(itemId, function (isEp) {
                        if (isEp !== true) {
                            removeButton();
                            return;
                        }

                        ensureButton();
                    });
                });
            }

            // 轮询 + hashchange 双保险：Emby 的 SPA 切换视图时可能改 hash，也可能只重渲染 DOM，
            // 只有轮询能同时覆盖这两种情况；hashchange 用来让响应更即时。
            window.setInterval(tick, POLL_INTERVAL_MS);
            window.addEventListener('hashchange', tick, false);
            window.addEventListener('popstate', tick, false);
            tick();
        } catch (e) {
            consoleError('详情页按钮初始化失败。', e);
        }
    }());

    // =====================================================================
    // 分支 B：设置页（configPage.html 的 data-controller 目标）
    // =====================================================================

    (function registerConfigPageModule() {
        try {
            // 设置页容器不在 DOM 中，说明这次加载不是为设置页服务的：什么都不做。
            var container = document.getElementById('overviewTranslatorConfigPage');
            if (!container) {
                return;
            }

            // 只注册一次，避免同一页面重复注册匿名模块。
            if (window.__overviewTranslatorAmdDefined) {
                return;
            }

            if (typeof define !== 'function' || !define.amd) {
                consoleError('设置页需要 AMD 加载器（define/define.amd），当前环境没有。');
                return;
            }

            window.__overviewTranslatorAmdDefined = true;

            define(['baseView'], function (BaseView) {
                'use strict';

                var CONTAINER_ID = 'overviewTranslatorConfigPage';

                function findContainer() {
                    return document.querySelector('#' + CONTAINER_ID + ':not(.hide)')
                        || document.getElementById(CONTAINER_ID);
                }

                /** 读 "A.B.C" 形式的字段路径。 */
                function getPath(obj, path) {
                    var parts = path.split('.');
                    var current = obj;

                    for (var i = 0; i < parts.length; i++) {
                        if (current == null) {
                            return undefined;
                        }

                        current = current[parts[i]];
                    }

                    return current;
                }

                /** 写 "A.B.C" 形式的字段路径，中间层缺失时自动建对象。 */
                function setPath(obj, path, value) {
                    var parts = path.split('.');
                    var current = obj;

                    for (var i = 0; i < parts.length - 1; i++) {
                        if (current[parts[i]] == null || typeof current[parts[i]] !== 'object') {
                            current[parts[i]] = {};
                        }

                        current = current[parts[i]];
                    }

                    current[parts[parts.length - 1]] = value;
                }

                function eachField(root, callback) {
                    var elements = root.querySelectorAll('[data-field]');
                    for (var i = 0; i < elements.length; i++) {
                        callback(elements[i], elements[i].getAttribute('data-field'));
                    }
                }

                function View() {
                    BaseView.apply(this, arguments);

                    var container = findContainer();
                    if (!container) {
                        return;
                    }

                    if (!container.__otBound) {
                        container.__otBound = true;
                        bindEvents(container);
                    }

                    loadSettings(container);
                }

                Object.assign(View.prototype, BaseView.prototype);

                View.prototype.onResume = function () {
                    BaseView.prototype.onResume.apply(this, arguments);

                    var container = findContainer();
                    if (container && container.__otInitialized) {
                        loadStatus(container);
                    }
                };

                function getMessageElement(container, id) {
                    return container.querySelector('#' + id);
                }

                function setMessage(container, text, isError) {
                    var el = getMessageElement(container, 'otMessage');
                    if (!el) {
                        return;
                    }

                    el.textContent = text;
                    el.className = 'ot-status ' + (isError ? 'ot-status-error' : 'ot-status-ok');
                }

                function setText(container, id, text) {
                    var el = container.querySelector('#' + id);
                    if (el) {
                        el.textContent = text;
                    }
                }

                function setBusy(container, isBusy) {
                    var buttons = ['otSave', 'otTest', 'otBatch', 'otRefreshStatus'];
                    for (var i = 0; i < buttons.length; i++) {
                        var el = container.querySelector('#' + buttons[i]);
                        if (el) {
                            el.disabled = !!isBusy;
                        }
                    }
                }

                function bindEvents(container) {
                    var save = container.querySelector('#otSave');
                    if (save) {
                        save.addEventListener('click', function () {
                            saveSettings(container);
                        });
                    }

                    var test = container.querySelector('#otTest');
                    if (test) {
                        test.addEventListener('click', function () {
                            testBackend(container);
                        });
                    }

                    var batch = container.querySelector('#otBatch');
                    if (batch) {
                        batch.addEventListener('click', function () {
                            startBatch(container);
                        });
                    }

                    var refresh = container.querySelector('#otRefreshStatus');
                    if (refresh) {
                        refresh.addEventListener('click', function () {
                            loadStatus(container);
                        });
                    }

                    // 引擎切换时立刻显示/隐藏对应字段，不必等保存。
                    var backend = container.querySelector('#otBackend');
                    if (backend) {
                        backend.addEventListener('change', function () {
                            applyBackendVisibility(container, backend.value);
                        });
                    }
                }

                /** 根据所选引擎显示相关设置块。 */
                function applyBackendVisibility(container, backendName) {
                    var groups = container.querySelectorAll('.ot-engine-group');
                    for (var i = 0; i < groups.length; i++) {
                        var group = groups[i];
                        var names = (group.getAttribute('data-backends') || '').split(/\s+/);
                        var visible = names.indexOf(backendName) >= 0;
                        if (visible) {
                            group.classList.remove('ot-hidden');
                        } else {
                            group.classList.add('ot-hidden');
                        }
                    }
                }

                function renderItemTypes(container, options, selected) {
                    var host = container.querySelector('#otItemTypes');
                    if (!host) {
                        return;
                    }

                    host.innerHTML = '';

                    var list = options && options.length ? options : ['Episode'];
                    for (var i = 0; i < list.length; i++) {
                        var wrapper = document.createElement('label');
                        wrapper.className = 'ot-check-label';

                        var box = document.createElement('input');
                        box.type = 'checkbox';
                        box.value = list[i];
                        box.checked = (selected || []).indexOf(list[i]) >= 0;

                        var span = document.createElement('span');
                        span.textContent = list[i];

                        wrapper.appendChild(box);
                        wrapper.appendChild(span);
                        host.appendChild(wrapper);
                    }
                }

                /** GET /OverviewTranslator/Settings -> 填充表单。 */
                function loadSettings(container) {
                    var api = getApiClient();
                    if (!api) {
                        setMessage(container, '无法访问 Emby API：当前会话已失效，请重新登录。', true);
                        return;
                    }

                    setBusy(container, true);

                    api.getJSON(api.getUrl('OverviewTranslator/Settings')).then(
                        function (settings) {
                            setBusy(container, false);

                            if (!settings) {
                                setMessage(container, '服务端没有返回设置内容。', true);
                                return;
                            }

                            var select = container.querySelector('#otBackend');
                            if (select) {
                                select.innerHTML = '';
                                var backends = settings.Backends || [];
                                for (var i = 0; i < backends.length; i++) {
                                    var option = document.createElement('option');
                                    option.value = backends[i].Name;
                                    option.textContent = backends[i].Label || backends[i].Name;
                                    select.appendChild(option);
                                }

                                if (settings.Backend) {
                                    select.value = settings.Backend;
                                }
                            }

                            eachField(container, function (el, path) {
                                var value = getPath(settings, path);

                                if (el.type === 'checkbox') {
                                    el.checked = !!value;
                                } else if (el.type === 'password') {
                                    // 明文密钥永远不会下发；这里永远是空的。
                                    el.value = '';
                                } else if (el.type === 'number') {
                                    el.value = value == null ? '' : value;
                                } else {
                                    el.value = value == null ? '' : value;
                                }
                            });

                            renderItemTypes(container, settings.ItemTypeOptions, settings.ItemTypes);

                            var clearApiKey = container.querySelector('#otClearApiKey');
                            if (clearApiKey) {
                                clearApiKey.checked = false;
                            }

                            var clearLibreKey = container.querySelector('#otClearLibreKey');
                            if (clearLibreKey) {
                                clearLibreKey.checked = false;
                            }

                            setText(
                                container,
                                'otApiKeyState',
                                settings.Llm && settings.Llm.HasApiKey
                                    ? '服务端已保存密钥（此处留空即保持不变）'
                                    : '服务端尚未保存密钥');

                            setText(
                                container,
                                'otLibreKeyState',
                                settings.Free && settings.Free.HasLibreTranslateApiKey
                                    ? '服务端已保存密钥（此处留空即保持不变）'
                                    : '服务端尚未保存密钥');

                            applyBackendVisibility(container, select ? select.value : settings.Backend);

                            container.__otInitialized = true;
                            setMessage(container, '设置已加载。', false);

                            loadStatus(container);
                        },
                        function (err) {
                            setBusy(container, false);
                            setMessage(container, '读取设置失败：' + describeError(err), true);
                        });
                }

                /** 把表单收集成 POST /Settings 的请求体。 */
                function collectSettings(container) {
                    var payload = {};

                    eachField(container, function (el, path) {
                        if (el.type === 'password') {
                            // 密码字段单独处理：留空 = 不修改。
                            return;
                        }

                        var value;
                        if (el.type === 'checkbox') {
                            value = !!el.checked;
                        } else if (el.type === 'number') {
                            if (el.value === '') {
                                // 留空 = 不修改这一项（服务端同样忽略 <= 0）。
                                return;
                            }

                            value = parseInt(el.value, 10);
                            if (isNaN(value)) {
                                return;
                            }
                        } else {
                            value = el.value;
                        }

                        setPath(payload, path, value);
                    });

                    var llm = payload.Llm || (payload.Llm = {});
                    var apiKeyEl = container.querySelector('#otLlmApiKey');
                    if (apiKeyEl && apiKeyEl.value) {
                        llm.ApiKey = apiKeyEl.value;
                    }

                    var clearApiKey = container.querySelector('#otClearApiKey');
                    llm.ClearApiKey = !!(clearApiKey && clearApiKey.checked);

                    var free = payload.Free || (payload.Free = {});
                    var libreKeyEl = container.querySelector('#otLibreKey');
                    if (libreKeyEl && libreKeyEl.value) {
                        free.LibreTranslateApiKey = libreKeyEl.value;
                    }

                    var clearLibreKey = container.querySelector('#otClearLibreKey');
                    free.ClearLibreTranslateApiKey = !!(clearLibreKey && clearLibreKey.checked);

                    var types = [];
                    var checked = container.querySelectorAll('#otItemTypes input[type=checkbox]');
                    for (var i = 0; i < checked.length; i++) {
                        if (checked[i].checked) {
                            types.push(checked[i].value);
                        }
                    }

                    if (types.length > 0) {
                        payload.ItemTypes = types;
                    }

                    return payload;
                }

                /** POST /OverviewTranslator/Settings。 */
                function saveSettings(container) {
                    var api = getApiClient();
                    if (!api) {
                        setMessage(container, '无法访问 Emby API：当前会话已失效，请重新登录。', true);
                        return;
                    }

                    setBusy(container, true);
                    setMessage(container, '正在保存…', false);

                    api.ajax({
                        type: 'POST',
                        url: api.getUrl('OverviewTranslator/Settings'),
                        data: JSON.stringify(collectSettings(container)),
                        contentType: 'application/json',
                        dataType: 'json'
                    }).then(
                        function () {
                            setBusy(container, false);
                            setMessage(container, '保存成功。', false);
                            loadSettings(container);
                        },
                        function (err) {
                            setBusy(container, false);
                            setMessage(container, '保存失败：' + describeError(err), true);
                        });
                }

                /** POST /OverviewTranslator/Test。 */
                function testBackend(container) {
                    var api = getApiClient();
                    if (!api) {
                        setMessage(container, '无法访问 Emby API：当前会话已失效，请重新登录。', true);
                        return;
                    }

                    setBusy(container, true);
                    setMessage(container, '正在测试后端，可能需要几秒钟…', false);

                    api.ajax({
                        type: 'POST',
                        url: api.getUrl('OverviewTranslator/Test'),
                        dataType: 'json'
                    }).then(
                        function (result) {
                            setBusy(container, false);
                            var ok = !!(result && result.Ok);
                            var text = (result && result.Message) || (ok ? '后端可用。' : '后端不可用。');
                            setMessage(container, (ok ? '✓ ' : '✗ ') + text, !ok);
                        },
                        function (err) {
                            setBusy(container, false);
                            setMessage(container, '测试失败：' + describeError(err), true);
                        });
                }

                /** POST /OverviewTranslator/Batch（后台执行，接口立即返回）。 */
                function startBatch(container) {
                    var api = getApiClient();
                    if (!api) {
                        setMessage(container, '无法访问 Emby API：当前会话已失效，请重新登录。', true);
                        return;
                    }

                    setBusy(container, true);
                    setMessage(container, '正在启动批量翻译…', false);

                    api.ajax({
                        type: 'POST',
                        url: api.getUrl('OverviewTranslator/Batch'),
                        dataType: 'json'
                    }).then(
                        function (result) {
                            setBusy(container, false);
                            setMessage(container, (result && result.Message) || '已提交批量翻译。', false);
                            loadStatus(container);
                        },
                        function (err) {
                            setBusy(container, false);
                            setMessage(container, '启动失败：' + describeError(err), true);
                        });
                }

                /** GET /OverviewTranslator/Status。 */
                function loadStatus(container) {
                    var api = getApiClient();
                    if (!api) {
                        setText(container, 'otStatus', '无法访问 Emby API。');
                        return;
                    }

                    api.getJSON(api.getUrl('OverviewTranslator/Status')).then(
                        function (status) {
                            if (!status) {
                                setText(container, 'otStatus', '状态不可用。');
                                return;
                            }

                            var lines = [];
                            lines.push('引擎：' + (status.Backend || '未知')
                                + (status.Enabled ? '' : '（插件已停用）'));

                            if (status.Pending < 0) {
                                lines.push('待翻译条目：统计失败'
                                    + (status.PendingError ? '（' + status.PendingError + '）' : ''));
                            } else {
                                lines.push('待翻译条目：' + status.Pending);
                            }

                            lines.push('批量任务：' + (status.Running
                                ? '运行中 ' + Math.round(status.LastRunPercent || 0) + '%'
                                : '空闲'));

                            if (status.LastRunMessage) {
                                lines.push('最近一次：' + status.LastRunMessage);
                            }

                            if (status.LastError) {
                                lines.push('错误：' + status.LastError);
                            }

                            setText(container, 'otStatus', lines.join('\n'));
                        },
                        function (err) {
                            setText(container, 'otStatus', '读取状态失败：' + describeError(err));
                        });
                }

                return View;
            });
        } catch (e) {
            consoleError('设置页模块注册失败。', e);
        }
    }());
}());
