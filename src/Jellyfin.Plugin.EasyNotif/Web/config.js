/*
 * Easy Notif - admin config page.
 * Two tabs: Settings (Resend credentials, sender, public URL, time zone) and User preferences
 * (one row per Jellyfin user, editable). Talks to the plugin's own /EasyNotif/admin/* endpoints,
 * not the stock plugin-configuration API, so secret values never reach the browser.
 *
 * Pure helpers are exported for Vitest; the browser section (after the export) never runs under
 * jsdom because `module` is defined there and the IIFE returns immediately.
 */

(function () {
    'use strict';

    var PLUGIN_ID = '7a27339e-e774-4969-9755-8cd213dcc5e7';

    function _escHtml(value) {
        if (value === null || value === undefined) {
            return '';
        }
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    // Language for the UI strings: 'fr' when the browser locale starts with fr, else 'en' (default).
    function _pickLang(navLang) {
        return String(navLang || '').toLowerCase().indexOf('fr') === 0 ? 'fr' : 'en';
    }

    // Translate a key against a loaded dictionary; fall back to the key itself so a missing string
    // is visible rather than silently blank.
    function _t(dict, key) {
        return (dict && dict[key]) || key;
    }

    // Static hint under a secret field. Never interpolates a value - the real secret stays server-side.
    function _secretHint(dict, isSet) {
        return isSet ? _t(dict, 'settings.secretSet') : _t(dict, 'settings.secretUnset');
    }

    function _buildPrefRow(dict, row) {
        var emailCell = row.hasEmail
            ? _escHtml(row.maskedEmail)
            : '<span class="enotif-warn">' + _escHtml(_t(dict, 'pref.noEmail')) + '</span>';

        function box(cat, checked) {
            return '<input type="checkbox" class="enotif-cat" data-user="' + _escHtml(row.userId)
                + '" data-cat="' + cat + '"' + (checked ? ' checked' : '') + ' />';
        }

        return '<tr>'
            + '<td>' + _escHtml(row.userName) + '</td>'
            + '<td>' + emailCell + '</td>'
            + '<td>' + box('news', row.news) + '</td>'
            + '<td>' + box('recap', row.recap) + '</td>'
            + '<td>' + (row.updatedAt ? _escHtml(String(row.updatedAt).slice(0, 10)) : '-') + '</td>'
            + '</tr>';
    }

    // Detached DOM built from the /admin/status payload. Every cell is set via textContent, so a
    // hostile stored value (a masked address, a webhook type) is data, never markup.
    function _buildStatus(dict, status) {
        var root = document.createElement('div');
        if (!status) {
            return root;
        }

        var configured = document.createElement('p');
        configured.textContent = _t(dict, status.configured ? 'status.configured' : 'status.notConfigured');
        root.appendChild(configured);

        var q = status.quota || {};
        var quota = document.createElement('p');
        quota.textContent = _t(dict, 'status.quota')
            .replace('{m30}', q.last30d != null ? q.last30d : 0)
            .replace('{mLimit}', q.monthlyLimit != null ? q.monthlyLimit : 0)
            .replace('{d}', q.dailyToday != null ? q.dailyToday : 0)
            .replace('{dLimit}', q.dailyLimit != null ? q.dailyLimit : 0);
        root.appendChild(quota);

        if (q.warn80) {
            var warn = document.createElement('p');
            warn.className = 'enotif-warn';
            warn.textContent = _t(dict, 'status.warn');
            root.appendChild(warn);
        }

        var webhook = document.createElement('p');
        webhook.textContent = status.lastWebhookUtc
            ? _t(dict, 'status.lastWebhook')
                .replace('{type}', status.lastWebhookType || '')
                .replace('{date}', String(status.lastWebhookUtc).slice(0, 19).replace('T', ' '))
            : _t(dict, 'status.lastWebhookNone');
        root.appendChild(webhook);

        var recent = status.recent || [];
        if (recent.length) {
            var table = document.createElement('table');
            var head = document.createElement('tr');
            ['status.col.date', 'status.col.context', 'status.col.recipient', 'status.col.status'].forEach(function (key) {
                var th = document.createElement('th');
                th.textContent = _t(dict, key);
                head.appendChild(th);
            });
            var thead = document.createElement('thead');
            thead.appendChild(head);
            table.appendChild(thead);

            var tbody = document.createElement('tbody');
            recent.forEach(function (entry) {
                var tr = document.createElement('tr');
                [
                    String(entry.ts || '').slice(0, 10),
                    entry.context || '',
                    entry.toMasked || '',
                    entry.status || ''
                ].forEach(function (value) {
                    var td = document.createElement('td');
                    td.textContent = value;
                    tr.appendChild(td);
                });
                tbody.appendChild(tr);
            });
            table.appendChild(tbody);
            root.appendChild(table);
        }

        return root;
    }

    var api = {
        _escHtml: _escHtml,
        _pickLang: _pickLang,
        _t: _t,
        _secretHint: _secretHint,
        _buildPrefRow: _buildPrefRow,
        _buildStatus: _buildStatus,
        PLUGIN_ID: PLUGIN_ID
    };

    if (typeof module !== 'undefined' && module.exports) {
        module.exports = api;
        return;
    }

    if (typeof document === 'undefined' || typeof window === 'undefined' || !window.ApiClient) {
        return;
    }

    // ---------------------------------------------------------------------------
    // Browser section - runs only on the real plugin config page.
    // ---------------------------------------------------------------------------

    var dict = {};

    function _el(id) {
        return document.getElementById(id);
    }

    function _url(path) {
        return window.ApiClient.getUrl('EasyNotif/' + path);
    }

    function _putJson(path, body) {
        return window.ApiClient.ajax({
            type: 'PUT',
            url: _url(path),
            data: JSON.stringify(body),
            contentType: 'application/json'
        });
    }

    function _applyStrings(root) {
        root.querySelectorAll('[data-i18n]').forEach(function (node) {
            node.textContent = _t(dict, node.getAttribute('data-i18n'));
        });
    }

    function _loadSettings() {
        return window.ApiClient.getJSON(_url('admin/settings')).then(function (s) {
            _el('enotifFromEmail').value = s.fromEmail || '';
            _el('enotifFromName').value = s.fromName || '';
            _el('enotifReplyTo').value = s.replyTo || '';
            _el('enotifPublicUrl').value = s.publicServerUrl || '';
            _el('enotifTimeZone').value = s.schedulerTimeZone || 'Europe/Paris';
            _el('enotifApiKey').value = '';
            _el('enotifWebhookSecret').value = '';
            _el('enotifApiKeyHint').textContent = _secretHint(dict, s.resendApiKeySet);
            _el('enotifWebhookSecretHint').textContent = _secretHint(dict, s.webhookSigningSecretSet);

            var warning = _el('enotifStartupWarning');
            warning.textContent = s.startupWarning || '';
            warning.hidden = !s.startupWarning;
        });
    }

    function _loadStatus() {
        return window.ApiClient.getJSON(_url('admin/status')).then(function (status) {
            var body = _el('enotifStatusBody');
            body.innerHTML = '';
            body.appendChild(_buildStatus(dict, status));
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not load the transport status:', err);
        });
    }

    function _saveSettings(e) {
        e.preventDefault();
        var body = {
            FromEmail: _el('enotifFromEmail').value,
            FromName: _el('enotifFromName').value,
            ReplyTo: _el('enotifReplyTo').value,
            PublicServerUrl: _el('enotifPublicUrl').value,
            SchedulerTimeZone: _el('enotifTimeZone').value
        };
        var key = _el('enotifApiKey').value.trim();
        var secret = _el('enotifWebhookSecret').value.trim();
        if (key) {
            body.ResendApiKey = key;
        }
        if (secret) {
            body.WebhookSigningSecret = secret;
        }

        window.Dashboard.showLoadingMsg();
        _putJson('admin/settings', body).then(function () {
            window.Dashboard.alert(_t(dict, 'common.saved'));
            return _loadSettings().then(_loadStatus);
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not save settings:', err);
            window.Dashboard.alert(_t(dict, 'common.saveError'));
        }).then(function () {
            window.Dashboard.hideLoadingMsg();
        });
    }

    function _loadPrefs() {
        return window.ApiClient.getJSON(_url('admin/preferences')).then(function (rows) {
            _el('enotifPrefBody').innerHTML = (rows || []).map(function (r) {
                return _buildPrefRow(dict, r);
            }).join('');
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not load preferences:', err);
            _el('enotifPrefBody').innerHTML = '';
        });
    }

    function _onCatToggle(e) {
        var box = e.target;
        if (!box.classList || !box.classList.contains('enotif-cat')) {
            return;
        }
        var body = {};
        body[box.getAttribute('data-cat') === 'news' ? 'News' : 'Recap'] = box.checked;
        _putJson('admin/preferences/' + box.getAttribute('data-user'), body).catch(function (err) {
            console.error('[EasyNotif Config] could not update a preference:', err);
            box.checked = !box.checked;
        });
    }

    function _selectTab(name) {
        document.querySelectorAll('.enotif-tab').forEach(function (t) {
            t.classList.toggle('enotif-active', t.getAttribute('data-tab') === name);
        });
        document.querySelectorAll('.enotif-panel').forEach(function (p) {
            p.hidden = p.getAttribute('data-panel') !== name;
        });
    }

    function _onShow() {
        var page = _el('easyNotifConfigPage');
        dict = {};
        window.ApiClient.getJSON(_url('strings/' + _pickLang(window.navigator.language) + '.json'))
            .catch(function () { return {}; })
            .then(function (loaded) {
                dict = loaded || {};
                _applyStrings(page);
                _loadSettings().then(_loadStatus);
                _loadPrefs();
            });
    }

    function _bind() {
        var page = _el('easyNotifConfigPage');
        if (!page) {
            return;
        }
        page.addEventListener('pageshow', _onShow);
        page.querySelectorAll('.enotif-tab').forEach(function (t) {
            t.addEventListener('click', function () { _selectTab(t.getAttribute('data-tab')); });
        });
        _el('enotifSettingsForm').addEventListener('submit', _saveSettings);
        _el('enotifPrefBody').addEventListener('change', _onCatToggle);
        _selectTab('settings');
    }

    _bind();
})();
