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

    // ---- Manual email tab (pure helpers) ------------------------------------

    var MANUAL_MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;

    function _manualBody(mode, value) {
        return mode === 'html' ? { html: value } : { text: value };
    }

    function _manualRecipients(mode, checkedIds) {
        return mode === 'selected'
            ? { recipientMode: 'selected', recipientUserIds: checkedIds }
            : { recipientMode: 'all' };
    }

    function _validEmail(value) {
        return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(String(value || '').trim());
    }

    // Returns an error key, or null when the form is ready to send.
    function _validateManual(state) {
        if (!String(state.subject || '').trim()) {
            return 'manual.error.subject';
        }
        if (!String(state.body || '').trim()) {
            return 'manual.error.body';
        }
        if (state.mode === 'test') {
            if (!_validEmail(state.testAddress)) {
                return 'manual.error.testAddress';
            }
        } else if (state.recipientMode === 'selected' && (!state.checkedIds || state.checkedIds.length === 0)) {
            return 'manual.error.recipients';
        }
        if ((state.attachmentBytes || 0) > MANUAL_MAX_ATTACHMENT_BYTES) {
            return 'manual.error.attachmentSize';
        }
        return null;
    }

    // Content for the sandboxed <iframe srcdoc>. HTML mode passes through (the iframe has an empty
    // sandbox, so scripts never run); text mode is escaped inside a <pre>.
    function _previewSrcdoc(mode, value) {
        return mode === 'html'
            ? String(value || '')
            : '<pre style="white-space:pre-wrap;font-family:inherit">' + _escHtml(value) + '</pre>';
    }

    function _fmtBytes(n) {
        if (n < 1024) { return n + ' B'; }
        if (n < 1024 * 1024) { return (n / 1024).toFixed(1) + ' KB'; }
        return (n / (1024 * 1024)).toFixed(1) + ' MB';
    }

    // Detached DOM for the send result. textContent only.
    function _manualResult(dict, summary) {
        var root = document.createElement('div');
        if (!summary) { return root; }

        var line = document.createElement('p');
        line.textContent = _t(dict, 'manual.result.summary')
            .replace('{sent}', summary.sent != null ? summary.sent : 0)
            .replace('{failed}', summary.failed != null ? summary.failed : 0)
            .replace('{skipped}', summary.skippedNoEmail != null ? summary.skippedNoEmail : 0);
        root.appendChild(line);

        (summary.details || []).forEach(function (d) {
            var item = document.createElement('div');
            item.textContent = (d.maskedTo || '') + ' - ' + (d.status || '');
            root.appendChild(item);
        });
        return root;
    }

    // Client-side level filter for the Logs tab. Lines are produced by EasyNotifLog with a fixed
    // "[DBG]"/"[INF]"/"[WRN]"/"[ERR]" marker, so no parsing beyond a substring check is needed.
    var LOG_LEVEL_MARKERS = { debug: '[DBG]', info: '[INF]', warn: '[WRN]', error: '[ERR]' };

    function _filterLogLines(lines, level) {
        var marker = LOG_LEVEL_MARKERS[level];
        if (!marker) {
            return lines || [];
        }
        return (lines || []).filter(function (line) { return line.indexOf(marker) !== -1; });
    }

    // ---- Campaigns tab (pure helpers) ------------------------------------

    var SCHEDULE_KINDS = ['daily', 'weekly', 'monthly', 'everyNDays'];
    var DOW = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

    // Which conditional inputs a recurrence kind needs, on top of the always-present time field.
    function _scheduleFieldsForKind(kind) {
        if (kind === 'weekly') { return ['time', 'dayOfWeek']; }
        if (kind === 'monthly') { return ['time', 'dayOfMonth']; }
        if (kind === 'everyNDays') { return ['time', 'intervalDays']; }
        return ['time'];
    }

    function _validTime(value) {
        return /^([01]\d|2[0-3]):[0-5]\d$/.test(String(value || ''));
    }

    // Returns an error key, or null when the schedule form is valid.
    function _validateSchedule(state) {
        if (!_validTime(state.time)) {
            return 'campaigns.error.time';
        }
        if (state.kind === 'weekly' && DOW.indexOf(state.dayOfWeek) === -1) {
            return 'campaigns.error.dayOfWeek';
        }
        if (state.kind === 'monthly' && !(state.dayOfMonth >= 1 && state.dayOfMonth <= 31)) {
            return 'campaigns.error.dayOfMonth';
        }
        if (state.kind === 'everyNDays' && !(state.intervalDays >= 1)) {
            return 'campaigns.error.intervalDays';
        }
        return null;
    }

    // The { kind, time, ... } payload for PUT /admin/campaigns/{id}, keyed to the chosen kind only.
    function _scheduleFromForm(state) {
        var out = { kind: state.kind, time: state.time };
        if (state.kind === 'weekly') { out.dayOfWeek = state.dayOfWeek; }
        if (state.kind === 'monthly') { out.dayOfMonth = state.dayOfMonth; }
        if (state.kind === 'everyNDays') { out.intervalDays = state.intervalDays; }
        return out;
    }

    // Human-readable one-liner for a schedule object from GET /admin/campaigns.
    function _fmtSchedule(dict, schedule) {
        if (!schedule) { return ''; }
        var time = schedule.time || '';
        if (schedule.kind === 'Weekly') {
            return _t(dict, 'campaigns.fmt.weekly')
                .replace('{day}', _t(dict, 'campaigns.dow.' + String(schedule.dayOfWeek || '').toLowerCase()))
                .replace('{time}', time);
        }
        if (schedule.kind === 'Monthly') {
            return _t(dict, 'campaigns.fmt.monthly').replace('{day}', schedule.dayOfMonth).replace('{time}', time);
        }
        if (schedule.kind === 'EveryNDays') {
            return _t(dict, 'campaigns.fmt.everyNDays').replace('{n}', schedule.intervalDays).replace('{time}', time);
        }
        return _t(dict, 'campaigns.fmt.daily').replace('{time}', time);
    }

    function _fmtDateTime(value) {
        return value ? String(value).slice(0, 16).replace('T', ' ') : null;
    }

    var api = {
        _escHtml: _escHtml,
        _pickLang: _pickLang,
        _t: _t,
        _secretHint: _secretHint,
        _buildPrefRow: _buildPrefRow,
        _buildStatus: _buildStatus,
        _manualBody: _manualBody,
        _manualRecipients: _manualRecipients,
        _validateManual: _validateManual,
        _previewSrcdoc: _previewSrcdoc,
        _fmtBytes: _fmtBytes,
        _manualResult: _manualResult,
        _filterLogLines: _filterLogLines,
        _scheduleFieldsForKind: _scheduleFieldsForKind,
        _validateSchedule: _validateSchedule,
        _scheduleFromForm: _scheduleFromForm,
        _fmtSchedule: _fmtSchedule,
        _fmtDateTime: _fmtDateTime,
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

    // ---- Manual email tab (browser) ---------------------------------------

    var manualFiles = [];
    var logLines = [];

    function _postJson(path, body) {
        return window.ApiClient.ajax({
            type: 'POST',
            url: _url(path),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function _manualMode() {
        var checked = document.querySelector('input[name="enotifManualMode"]:checked');
        return checked ? checked.value : 'text';
    }

    function _manualRcptMode() {
        var checked = document.querySelector('input[name="enotifManualRcpt"]:checked');
        return checked ? checked.value : 'all';
    }

    function _manualCheckedIds() {
        return Array.prototype.map.call(
            document.querySelectorAll('#enotifManualUsers input[type="checkbox"]:checked'),
            function (b) { return b.getAttribute('data-user-id'); });
    }

    function _loadManualUsers() {
        return window.ApiClient.getJSON(_url('admin/preferences')).then(function (rows) {
            var host = _el('enotifManualUsers');
            host.innerHTML = '';
            (rows || []).forEach(function (r) {
                var label = document.createElement('label');
                label.className = 'checkboxContainer';
                var box = document.createElement('input');
                box.type = 'checkbox';
                box.setAttribute('data-user-id', r.userId);
                box.disabled = !r.hasEmail;
                var text = document.createElement('span');
                text.textContent = r.userName + (r.hasEmail ? '' : ' (' + _t(dict, 'manual.recipients.noEmail') + ')');
                label.appendChild(box);
                label.appendChild(text);
                host.appendChild(label);
            });
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not load users:', err);
        });
    }

    function _readAttachments(fileList) {
        return Promise.all(Array.prototype.map.call(fileList, function (file) {
            return new Promise(function (resolve, reject) {
                var reader = new FileReader();
                reader.onload = function () {
                    var result = String(reader.result || '');
                    var comma = result.indexOf(',');
                    resolve({
                        fileName: file.name,
                        contentBase64: comma >= 0 ? result.slice(comma + 1) : result,
                        contentType: file.type || null,
                        size: file.size
                    });
                };
                reader.onerror = function () { reject(reader.error); };
                reader.readAsDataURL(file);
            });
        }));
    }

    function _renderFileList() {
        var host = _el('enotifManualFileList');
        host.innerHTML = '';
        manualFiles.forEach(function (f, i) {
            var li = document.createElement('li');
            var name = document.createElement('span');
            name.textContent = f.fileName + ' (' + _fmtBytes(f.size) + ')';
            var remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'raised';
            remove.textContent = _t(dict, 'manual.attachments.remove');
            remove.addEventListener('click', function () {
                manualFiles.splice(i, 1);
                _renderFileList();
            });
            li.appendChild(name);
            li.appendChild(remove);
            host.appendChild(li);
        });
    }

    function _refreshManualPreview() {
        _el('enotifManualPreview').srcdoc = _previewSrcdoc(_manualMode(), _el('enotifManualBody').value);
    }

    function _setManualResult(text, isError) {
        var result = _el('enotifManualResult');
        result.innerHTML = '';
        result.textContent = text;
        result.classList.toggle('enotif-status-error', !!isError);
    }

    function _sendManual(isTest) {
        var mode = _manualMode();
        var rcptMode = isTest ? 'test' : _manualRcptMode();
        var state = {
            subject: _el('enotifManualSubject').value,
            body: _el('enotifManualBody').value,
            mode: isTest ? 'test' : mode,
            recipientMode: rcptMode,
            checkedIds: _manualCheckedIds(),
            testAddress: _el('enotifManualTestAddress').value,
            attachmentBytes: manualFiles.reduce(function (sum, f) { return sum + f.size; }, 0)
        };

        var error = _validateManual(state);
        if (error) {
            _setManualResult(_t(dict, error), true);
            return;
        }

        var payload = { subject: state.subject };
        var bodyPart = _manualBody(mode, state.body);
        payload.html = bodyPart.html || null;
        payload.text = bodyPart.text || null;
        payload.attachments = manualFiles.map(function (f) {
            return { fileName: f.fileName, contentBase64: f.contentBase64, contentType: f.contentType };
        });

        if (isTest) {
            payload.recipientMode = 'test';
            payload.testAddress = state.testAddress;
        } else {
            var rcpt = _manualRecipients(rcptMode, state.checkedIds);
            payload.recipientMode = rcpt.recipientMode;
            payload.recipientUserIds = rcpt.recipientUserIds || null;
        }

        _setManualResult(_t(dict, 'manual.sending'), false);
        window.Dashboard.showLoadingMsg();
        _postJson('admin/send', payload).then(function (summary) {
            var result = _el('enotifManualResult');
            result.innerHTML = '';
            result.classList.remove('enotif-status-error');
            result.appendChild(_manualResult(dict, summary));
        }).catch(function (err) {
            console.error('[EasyNotif Config] manual send failed:', err);
            _setManualResult(_t(dict, 'manual.error.send'), true);
        }).then(function () {
            window.Dashboard.hideLoadingMsg();
        });
    }

    function _bindManual() {
        _el('enotifManualPreviewBtn').addEventListener('click', _refreshManualPreview);
        _el('enotifManualTestBtn').addEventListener('click', function () { _sendManual(true); });
        _el('enotifManualSendBtn').addEventListener('click', function () { _sendManual(false); });
        document.querySelectorAll('input[name="enotifManualRcpt"]').forEach(function (radio) {
            radio.addEventListener('change', function () {
                _el('enotifManualUsers').hidden = _manualRcptMode() !== 'selected';
            });
        });
        _el('enotifManualFiles').addEventListener('change', function (e) {
            _readAttachments(e.target.files).then(function (files) {
                manualFiles = manualFiles.concat(files);
                e.target.value = '';
                _renderFileList();
            });
        });
    }

    // ---- Logs tab (browser) -------------------------------------------------

    function _renderLogView() {
        var level = _el('enotifLogLevel').value;
        var filtered = _filterLogLines(logLines, level);
        _el('enotifLogView').textContent = filtered.length ? filtered.join('\n') : _t(dict, 'logs.empty');
    }

    function _loadLogs() {
        return window.ApiClient.getJSON(_url('admin/logs?tail=200')).then(function (lines) {
            logLines = lines || [];
            _renderLogView();
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not load logs:', err);
        });
    }

    function _bindLogs() {
        _el('enotifLogRefresh').addEventListener('click', _loadLogs);
        _el('enotifLogLevel').addEventListener('change', _renderLogView);
    }

    // ---- Campaigns tab (browser) -----------------------------------------

    function _campaignFormState(root) {
        return {
            kind: root.querySelector('.enotif-campaign-kind').value,
            time: root.querySelector('.enotif-campaign-time').value,
            dayOfWeek: root.querySelector('.enotif-campaign-dow').value,
            dayOfMonth: parseInt(root.querySelector('.enotif-campaign-dom').value, 10),
            intervalDays: parseInt(root.querySelector('.enotif-campaign-interval').value, 10)
        };
    }

    function _applyKindVisibility(root) {
        var fields = _scheduleFieldsForKind(root.querySelector('.enotif-campaign-kind').value);
        root.querySelector('.enotif-campaign-dow-row').hidden = fields.indexOf('dayOfWeek') === -1;
        root.querySelector('.enotif-campaign-dom-row').hidden = fields.indexOf('dayOfMonth') === -1;
        root.querySelector('.enotif-campaign-interval-row').hidden = fields.indexOf('intervalDays') === -1;
    }

    function _labeledRow(cls, labelKey, control) {
        var row = document.createElement('div');
        row.className = 'enotif-campaign-row ' + cls;
        var label = document.createElement('label');
        label.setAttribute('data-i18n', labelKey);
        label.textContent = _t(dict, labelKey);
        row.appendChild(label);
        row.appendChild(control);
        return row;
    }

    function _select(cls, options) {
        var sel = document.createElement('select');
        sel.className = 'emby-select ' + cls;
        options.forEach(function (o) {
            var opt = document.createElement('option');
            opt.value = o.value;
            opt.textContent = o.label;
            sel.appendChild(opt);
        });
        return sel;
    }

    function _numberInput(cls, min, max) {
        var input = document.createElement('input');
        input.type = 'number';
        input.className = 'emby-input ' + cls;
        input.min = String(min);
        if (max != null) { input.max = String(max); }
        return input;
    }

    function _buildCampaignCard(campaign) {
        var root = document.createElement('div');
        root.className = 'enotif-campaign';
        root.setAttribute('data-campaign-id', campaign.id);

        var title = document.createElement('h3');
        title.className = 'enotif-campaign-title';
        title.textContent = _t(dict, 'campaigns.name.' + campaign.id);
        root.appendChild(title);

        var enabled = document.createElement('input');
        enabled.type = 'checkbox';
        enabled.className = 'enotif-campaign-enabled';
        enabled.checked = !!campaign.enabled;
        root.appendChild(_labeledRow('', 'campaigns.enabled', enabled));

        var lang = _select('enotif-campaign-lang', [{ value: 'fr', label: 'Francais' }, { value: 'en', label: 'English' }]);
        lang.value = campaign.mailLanguage || 'fr';
        root.appendChild(_labeledRow('', 'campaigns.language', lang));

        var kind = _select('enotif-campaign-kind', SCHEDULE_KINDS.map(function (k) {
            return { value: k, label: _t(dict, 'campaigns.kind.' + k) };
        }));
        var kindName = String(campaign.schedule.kind || 'Daily');
        kind.value = kindName.charAt(0).toLowerCase() + kindName.slice(1);
        root.appendChild(_labeledRow('', 'campaigns.recurrence', kind));

        var time = document.createElement('input');
        time.type = 'time';
        time.className = 'emby-input enotif-campaign-time';
        time.value = campaign.schedule.time || '09:00';
        root.appendChild(_labeledRow('', 'campaigns.time', time));

        var dow = _select('enotif-campaign-dow', DOW.map(function (d) {
            return { value: d, label: _t(dict, 'campaigns.dow.' + d.toLowerCase()) };
        }));
        dow.value = campaign.schedule.dayOfWeek || 'Monday';
        root.appendChild(_labeledRow('enotif-campaign-dow-row', 'campaigns.dayOfWeek', dow));

        var dom = _numberInput('enotif-campaign-dom', 1, 31);
        dom.value = String(campaign.schedule.dayOfMonth || 1);
        root.appendChild(_labeledRow('enotif-campaign-dom-row', 'campaigns.dayOfMonth', dom));

        var interval = _numberInput('enotif-campaign-interval', 1, null);
        interval.value = String(campaign.schedule.intervalDays || 1);
        root.appendChild(_labeledRow('enotif-campaign-interval-row', 'campaigns.intervalDays', interval));

        var meta = document.createElement('div');
        meta.className = 'enotif-campaign-meta';
        meta.textContent = _t(dict, 'campaigns.lastRun').replace('{value}', _fmtDateTime(campaign.lastSentUtc) || _t(dict, 'campaigns.never'))
            + '  |  '
            + _t(dict, 'campaigns.nextRun').replace('{value}', _fmtDateTime(campaign.nextRunUtc) || _t(dict, 'campaigns.never'));
        root.appendChild(meta);

        var actions = document.createElement('div');
        actions.className = 'enotif-campaign-row';
        var save = document.createElement('button');
        save.type = 'button';
        save.className = 'raised button-submit enotif-campaign-save';
        save.textContent = _t(dict, 'campaigns.save');
        var run = document.createElement('button');
        run.type = 'button';
        run.className = 'raised enotif-campaign-run';
        run.textContent = _t(dict, 'campaigns.run');
        actions.appendChild(save);
        actions.appendChild(run);
        root.appendChild(actions);

        var result = document.createElement('div');
        result.className = 'enotif-campaign-result';
        result.setAttribute('role', 'status');
        root.appendChild(result);

        kind.addEventListener('change', function () { _applyKindVisibility(root); });
        save.addEventListener('click', function () { _saveCampaign(campaign.id, root); });
        run.addEventListener('click', function () { _runCampaign(campaign.id, root); });
        _applyKindVisibility(root);
        return root;
    }

    function _loadCampaigns() {
        return window.ApiClient.getJSON(_url('admin/campaigns')).then(function (campaigns) {
            var host = _el('enotifCampaignList');
            host.innerHTML = '';
            (campaigns || []).forEach(function (c) { host.appendChild(_buildCampaignCard(c)); });
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not load campaigns:', err);
        });
    }

    function _saveCampaign(id, root) {
        var state = _campaignFormState(root);
        var error = _validateSchedule(state);
        var result = root.querySelector('.enotif-campaign-result');
        if (error) {
            result.classList.add('enotif-status-error');
            result.textContent = _t(dict, error);
            return;
        }

        var body = {
            enabled: root.querySelector('.enotif-campaign-enabled').checked,
            mailLanguage: root.querySelector('.enotif-campaign-lang').value,
            schedule: _scheduleFromForm(state)
        };

        window.Dashboard.showLoadingMsg();
        _putJson('admin/campaigns/' + id, body).then(function () {
            result.classList.remove('enotif-status-error');
            result.textContent = _t(dict, 'campaigns.saved');
            return _loadCampaigns();
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not save a campaign:', err);
            result.classList.add('enotif-status-error');
            result.textContent = _t(dict, 'common.saveError');
        }).then(function () {
            window.Dashboard.hideLoadingMsg();
        });
    }

    function _runCampaign(id, root) {
        var result = root.querySelector('.enotif-campaign-result');
        result.classList.remove('enotif-status-error');
        result.textContent = _t(dict, 'manual.sending');
        window.Dashboard.showLoadingMsg();
        _postJson('admin/campaigns/' + id + '/run', {}).then(function (summary) {
            result.textContent = _t(dict, 'campaigns.runResult')
                .replace('{sent}', summary.sent != null ? summary.sent : 0)
                .replace('{failed}', summary.failed != null ? summary.failed : 0)
                .replace('{skipped}', summary.skippedNoEmail != null ? summary.skippedNoEmail : 0);
            return _loadCampaigns();
        }).catch(function (err) {
            console.error('[EasyNotif Config] could not run a campaign:', err);
            result.classList.add('enotif-status-error');
            result.textContent = _t(dict, 'manual.error.send');
        }).then(function () {
            window.Dashboard.hideLoadingMsg();
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
                _loadLogs();
                _loadCampaigns();
                _loadManualUsers().then(function () {
                    var addr = _el('enotifManualTestAddress');
                    if (!addr.value) {
                        window.ApiClient.getJSON(_url('me/contact-email'))
                            .then(function (r) { if (r && r.email) { addr.value = r.email; } })
                            .catch(function () { /* optional */ });
                    }
                });
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
        _bindManual();
        _bindLogs();
        _selectTab('settings');
    }

    _bind();
})();
