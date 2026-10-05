// ==UserScript==
// @name         Emby 简介翻译按钮
// @namespace    local.overviewtranslator
// @version      1.0
// @description  在 Emby 详情页加一个「翻译简介」按钮，调用 Overview Translator 插件
// @match        http://localhost:8096/web/*
// @match        http://127.0.0.1:8096/web/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==

/*
 * 为什么需要这个脚本
 * ----------------
 * Emby 4.10 **不会**把插件的 .js 全局注入到网页里（实测 /web/index.html 里没有任何插件脚本
 * 引用，连官方 Bangumi 插件也没有）。插件页的脚本只有在「设置页被打开」时才由前端加载。
 * 因此详情页的按钮没法由插件自己挂上去，只能靠浏览器这一侧注入。
 *
 * 安装方式（二选一）
 * ----------------
 * A. 用户脚本管理器（推荐，一次安装长期有效）
 *    安装 Tampermonkey / Violentmonkey，新建脚本，把本文件内容粘进去保存。
 *    如果你的 Emby 不在 localhost:8096，改上面的 @match 行。
 *
 * B. 临时用（不想装扩展）
 *    打开 Emby 页面，按 F12 → Console，把下面 <script> 里的代码整段粘进去回车。
 *    每次刷新页面都要重来一次。
 *
 * 用法：打开任意剧集详情页，右下角会出现「翻译简介」按钮，点击即翻译当前条目。
 */

(function () {
    'use strict';

    // 服务器地址直接复用当前页面，避免多填一份配置。
    var server = window.location.origin;
    var apiKey = '';

    // Emby 的网页客户端把访问令牌放在 localStorage / 请求头里。为了不改动 Emby 的存储，
    // 优先从页面上已有的 ApiClient 取；取不到就让用户在弹窗里填一次，存在本脚本自己的
    // localStorage 键里。
    function getToken() {
        if (apiKey) {
            return apiKey;
        }

        try {
            var stored = window.localStorage.getItem('overviewTranslatorToken');
            if (stored) {
                apiKey = stored;
                return apiKey;
            }
        } catch (e) {
        }

        // Emby 网页版的 ApiClient 会缓存令牌。
        try {
            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                apiKey = window.ApiClient.accessToken();
                return apiKey;
            }
        } catch (e) {
        }

        apiKey = window.prompt('请输入 Emby API Key（控制台 → 高级 → API 密钥）', '');
        if (apiKey) {
            try {
                window.localStorage.setItem('overviewTranslatorToken', apiKey);
            } catch (e) {
            }
        }

        return apiKey;
    }

    // Emby 的地址形如 #!/item?id=14177&...，条目 id 从 hash 里取。
    function getItemId() {
        var hash = window.location.hash || '';
        var match = hash.match(/[?&]id=([0-9a-fA-F]+)/);
        return match ? match[1] : null;
    }

    function toast(message, isError) {
        var box = document.createElement('div');
        box.textContent = message;
        box.style.cssText = [
            'position:fixed', 'right:20px', 'bottom:80px', 'z-index:999999',
            'max-width:420px', 'padding:12px 16px', 'border-radius:6px',
            'font-size:14px', 'line-height:1.5', 'color:#fff',
            'box-shadow:0 4px 16px rgba(0,0,0,.4)',
            'background:' + (isError ? '#c62828' : '#2e7d32')
        ].join(';');
        document.body.appendChild(box);
        setTimeout(function () {
            box.remove();
        }, isError ? 9000 : 4500);
    }

    function translate() {
        var itemId = getItemId();
        if (!itemId) {
            toast('没有识别到条目 id，请在某个剧集/影片的详情页使用', true);
            return;
        }

        var token = getToken();
        if (!token) {
            toast('没有 API Key，无法调用翻译接口', true);
            return;
        }

        var button = document.getElementById('overviewTranslatorButton');
        if (button) {
            button.disabled = true;
            button.textContent = '翻译中…';
        }

        fetch(server + '/OverviewTranslator/Translate/' + itemId, {
            method: 'POST',
            headers: { 'X-Emby-Token': token }
        })
            .then(function (response) {
                return response.text().then(function (text) {
                    var data;
                    try {
                        data = JSON.parse(text);
                    } catch (e) {
                        data = null;
                    }

                    if (!response.ok) {
                        throw new Error((data && (data.Message || data.message)) || ('HTTP ' + response.status));
                    }

                    return data;
                });
            })
            .then(function (outcome) {
                if (!outcome) {
                    toast('接口没有返回内容', true);
                    return;
                }

                if (outcome.Saved) {
                    toast('翻译完成，刷新页面即可看到中文简介');
                } else if (outcome.Skipped) {
                    toast('已跳过：' + (outcome.Reason || '无需翻译'));
                } else if (outcome.Translated) {
                    toast('已翻译但未能写回 Emby：' + (outcome.Reason || '未知原因'), true);
                } else {
                    toast('翻译失败：' + (outcome.Reason || '未知原因'), true);
                }
            })
            .catch(function (error) {
                toast('翻译失败：' + error.message, true);
            })
            .then(function () {
                var current = document.getElementById('overviewTranslatorButton');
                if (current) {
                    current.disabled = false;
                    current.textContent = '翻译简介';
                }
            });
    }

    // 幂等注入：只创建一次按钮，SPA 路由变化时复用它。
    function ensureButton() {
        if (document.getElementById('overviewTranslatorButton')) {
            return;
        }

        if (!document.body) {
            return;
        }

        var button = document.createElement('button');
        button.id = 'overviewTranslatorButton';
        button.type = 'button';
        button.textContent = '翻译简介';
        button.title = '用 Overview Translator 插件翻译当前条目的简介';
        button.style.cssText = [
            'position:fixed', 'right:20px', 'bottom:24px', 'z-index:999998',
            'padding:10px 18px', 'border:0', 'border-radius:22px',
            'background:#52b54b', 'color:#fff', 'font-size:14px', 'cursor:pointer',
            'box-shadow:0 3px 12px rgba(0,0,0,.35)'
        ].join(';');
        button.addEventListener('click', translate);

        document.body.appendChild(button);
    }

    ensureButton();
    window.addEventListener('hashchange', ensureButton);
    setInterval(ensureButton, 1500);

    console.log('[Overview Translator] 用户脚本已加载，按钮应出现在右下角');
})();
