import { describe, it, expect } from 'vitest';
import enotifConfig from '../src/Jellyfin.Plugin.EasyNotif/Web/config.js';

import enStrings from '../src/Jellyfin.Plugin.EasyNotif/Web/strings/en.json';
import frStrings from '../src/Jellyfin.Plugin.EasyNotif/Web/strings/fr.json';

const {
    _escHtml, _pickLang, _t, _secretHint, _buildPrefRow, _buildStatus,
    _manualBody, _manualRecipients, _validateManual, _previewSrcdoc, _manualResult, _fmtBytes,
    _filterLogLines,
    _scheduleFieldsForKind, _validateSchedule, _scheduleFromForm, _fmtSchedule, _fmtDateTime,
    _previewResult, _tplOptions, _campaignTemplateBaseId,
    PLUGIN_ID
} = enotifConfig;

describe('_escHtml', () => {
    it('escapes HTML special characters', () => {
        expect(_escHtml('<b>Tom & "Jerry"</b>')).toBe('&lt;b&gt;Tom &amp; &quot;Jerry&quot;&lt;/b&gt;');
    });

    it('returns an empty string for nullish input', () => {
        expect(_escHtml(null)).toBe('');
        expect(_escHtml(undefined)).toBe('');
    });
});

describe('_pickLang', () => {
    it('picks fr when the locale starts with fr', () => {
        expect(_pickLang('fr')).toBe('fr');
        expect(_pickLang('fr-FR')).toBe('fr');
        expect(_pickLang('FR-ca')).toBe('fr');
    });

    it('defaults to en for anything else', () => {
        expect(_pickLang('en-US')).toBe('en');
        expect(_pickLang('de')).toBe('en');
        expect(_pickLang('')).toBe('en');
        expect(_pickLang(undefined)).toBe('en');
    });
});

describe('_t', () => {
    it('returns the translation for a known key', () => {
        expect(_t({ 'tab.settings': 'Réglages' }, 'tab.settings')).toBe('Réglages');
    });

    it('falls back to the key itself when missing', () => {
        expect(_t({}, 'tab.settings')).toBe('tab.settings');
        expect(_t(null, 'x')).toBe('x');
    });
});

describe('_secretHint', () => {
    it('uses the "set" string when a secret is stored, and never contains a value', () => {
        const dict = { 'settings.secretSet': 'A value is saved.', 'settings.secretUnset': 'None yet.' };
        expect(_secretHint(dict, true)).toBe('A value is saved.');
        expect(_secretHint(dict, false)).toBe('None yet.');
        expect(_secretHint(dict, true)).not.toMatch(/re_|whsec_/);
    });
});

describe('_buildPrefRow', () => {
    const dict = { 'pref.noEmail': 'no contact address' };

    it('renders the masked email and two checkboxes reflecting the opt-in state', () => {
        const html = _buildPrefRow(dict, {
            userId: 'u-1',
            userName: 'Alice',
            maskedEmail: 'a***e@example.org',
            hasEmail: true,
            news: true,
            recap: false,
            updatedAt: '2026-09-04T10:00:00Z'
        });

        expect(html).toContain('<td>Alice</td>');
        expect(html).toContain('a***e@example.org');
        expect(html).toContain('data-user="u-1" data-cat="news" checked');
        expect(html).toContain('data-user="u-1" data-cat="recap" />');
        expect(html).toContain('<td>2026-09-04</td>');
    });

    it('shows the no-address warning and no date when the user has no email', () => {
        const html = _buildPrefRow(dict, {
            userId: 'u-2',
            userName: 'Bob',
            maskedEmail: '(none)',
            hasEmail: false,
            news: false,
            recap: false,
            updatedAt: null
        });

        expect(html).toContain('enotif-warn');
        expect(html).toContain('no contact address');
        expect(html).toContain('<td>-</td>');
    });

    it('escapes a user name that contains markup', () => {
        const html = _buildPrefRow(dict, {
            userId: 'u-3',
            userName: '<script>alert(1)</script>',
            maskedEmail: '(none)',
            hasEmail: false,
            news: false,
            recap: false,
            updatedAt: null
        });

        expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
        expect(html).not.toContain('<script>');
    });
});

describe('_buildStatus', () => {
    const dict = enStrings;

    it('shows no warning class for a nominal quota', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: { last30d: 3, dailyToday: 1, monthlyLimit: 3000, dailyLimit: 100, warn80: false, over: false },
            lastWebhookUtc: null,
            recent: []
        });

        expect(el.querySelector('.enotif-warn')).toBeNull();
        expect(el.textContent).toContain('configured');
    });

    it('shows the warning when warn80 is set', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: { last30d: 2400, dailyToday: 10, monthlyLimit: 3000, dailyLimit: 100, warn80: true, over: false },
            lastWebhookUtc: null,
            recent: []
        });

        expect(el.querySelector('.enotif-warn')).not.toBeNull();
    });

    it('says no webhook when lastWebhookUtc is null', () => {
        const el = _buildStatus(dict, { configured: true, quota: {}, lastWebhookUtc: null, recent: [] });

        expect(el.textContent).toContain(dict['status.lastWebhookNone']);
    });

    it('renders a recent entry as text, never markup', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: {},
            lastWebhookUtc: null,
            recent: [{ ts: '2026-09-04T10:00:00Z', context: 'test', toMasked: '<img src=x onerror=alert(1)>', status: 'sent' }]
        });

        expect(el.innerHTML).not.toContain('<img');
        expect(el.textContent).toContain('<img src=x onerror=alert(1)>');
    });

    it('returns an empty container for a null payload', () => {
        expect(_buildStatus(dict, null).childNodes.length).toBe(0);
    });
});

describe('string bundles', () => {
    it('en.json and fr.json have the same keys', () => {
        expect(Object.keys(frStrings).sort()).toEqual(Object.keys(enStrings).sort());
    });
});

describe('manual email helpers', () => {
    it('_manualBody maps mode to the right payload field', () => {
        expect(_manualBody('html', '<p>x</p>')).toEqual({ html: '<p>x</p>' });
        expect(_manualBody('text', 'hi')).toEqual({ text: 'hi' });
    });

    it('_manualRecipients returns all, or selected with ids', () => {
        expect(_manualRecipients('all', ['a'])).toEqual({ recipientMode: 'all' });
        expect(_manualRecipients('selected', ['a', 'b'])).toEqual({ recipientMode: 'selected', recipientUserIds: ['a', 'b'] });
    });

    it('_validateManual returns null for a valid form', () => {
        expect(_validateManual({ subject: 'Hi', body: 'Body', mode: 'send', recipientMode: 'all', attachmentBytes: 0 })).toBeNull();
    });

    it('_validateManual flags each problem', () => {
        expect(_validateManual({ subject: ' ', body: 'x', recipientMode: 'all' })).toBe('manual.error.subject');
        expect(_validateManual({ subject: 'Hi', body: '', recipientMode: 'all' })).toBe('manual.error.body');
        expect(_validateManual({ subject: 'Hi', body: 'x', recipientMode: 'selected', checkedIds: [] })).toBe('manual.error.recipients');
        expect(_validateManual({ subject: 'Hi', body: 'x', mode: 'test', testAddress: 'nope' })).toBe('manual.error.testAddress');
        expect(_validateManual({ subject: 'Hi', body: 'x', mode: 'test', testAddress: 'a@b.co' })).toBeNull();
        expect(_validateManual({ subject: 'Hi', body: 'x', recipientMode: 'all', attachmentBytes: 11 * 1024 * 1024 })).toBe('manual.error.attachmentSize');
    });

    it('_previewSrcdoc passes HTML through and escapes text mode', () => {
        expect(_previewSrcdoc('html', '<h1>Hi</h1>')).toBe('<h1>Hi</h1>');
        const text = _previewSrcdoc('text', '<script>alert(1)</script>');
        expect(text).not.toContain('<script>');
        expect(text).toContain('&lt;script&gt;');
    });

    it('_manualResult renders a hostile maskedTo as text, not markup', () => {
        const el = _manualResult(enStrings, {
            sent: 1, failed: 0, skippedNoEmail: 0,
            details: [{ maskedTo: '<img src=x onerror=alert(1)>', status: 'sent' }]
        });
        expect(el.innerHTML).not.toContain('<img');
        expect(el.textContent).toContain('<img src=x onerror=alert(1)>');
    });

    it('_fmtBytes is human readable', () => {
        expect(_fmtBytes(512)).toBe('512 B');
        expect(_fmtBytes(2048)).toBe('2.0 KB');
        expect(_fmtBytes(3 * 1024 * 1024)).toBe('3.0 MB');
    });
});

describe('_filterLogLines', () => {
    const lines = [
        '2026-09-04 [DBG] tick a=1',
        '2026-09-04 [INF] plugin.startup version=0.5.0.0',
        '2026-09-04 [WRN] quota.threshold last30d=2400',
        '2026-09-04 [ERR] email.failed httpStatus=401'
    ];

    it('returns every line for "all"', () => {
        expect(_filterLogLines(lines, 'all')).toEqual(lines);
    });

    it('keeps only the lines matching the requested level', () => {
        expect(_filterLogLines(lines, 'error')).toEqual([lines[3]]);
        expect(_filterLogLines(lines, 'warn')).toEqual([lines[2]]);
        expect(_filterLogLines(lines, 'info')).toEqual([lines[1]]);
        expect(_filterLogLines(lines, 'debug')).toEqual([lines[0]]);
    });

    it('handles an empty or missing line list', () => {
        expect(_filterLogLines([], 'error')).toEqual([]);
        expect(_filterLogLines(undefined, 'all')).toEqual([]);
    });
});

describe('campaign schedule helpers', () => {
    it('_scheduleFieldsForKind returns the conditional fields per kind', () => {
        expect(_scheduleFieldsForKind('daily')).toEqual(['time']);
        expect(_scheduleFieldsForKind('weekly')).toEqual(['time', 'dayOfWeek']);
        expect(_scheduleFieldsForKind('monthly')).toEqual(['time', 'dayOfMonth']);
        expect(_scheduleFieldsForKind('everyNDays')).toEqual(['time', 'intervalDays']);
    });

    it('_validateSchedule flags each branch and passes a valid form', () => {
        expect(_validateSchedule({ kind: 'daily', time: '9:00' })).toBe('campaigns.error.time');
        expect(_validateSchedule({ kind: 'daily', time: '09:00' })).toBeNull();
        expect(_validateSchedule({ kind: 'weekly', time: '09:00', dayOfWeek: 'Funday' })).toBe('campaigns.error.dayOfWeek');
        expect(_validateSchedule({ kind: 'weekly', time: '09:00', dayOfWeek: 'Friday' })).toBeNull();
        expect(_validateSchedule({ kind: 'monthly', time: '09:00', dayOfMonth: 0 })).toBe('campaigns.error.dayOfMonth');
        expect(_validateSchedule({ kind: 'monthly', time: '09:00', dayOfMonth: 15 })).toBeNull();
        expect(_validateSchedule({ kind: 'everyNDays', time: '09:00', intervalDays: 0 })).toBe('campaigns.error.intervalDays');
        expect(_validateSchedule({ kind: 'everyNDays', time: '09:00', intervalDays: 3 })).toBeNull();
    });

    it('_scheduleFromForm only carries the fields the kind needs', () => {
        expect(_scheduleFromForm({ kind: 'weekly', time: '09:00', dayOfWeek: 'Friday', dayOfMonth: 5 }))
            .toEqual({ kind: 'weekly', time: '09:00', dayOfWeek: 'Friday' });
        expect(_scheduleFromForm({ kind: 'daily', time: '08:00', dayOfWeek: 'Friday' }))
            .toEqual({ kind: 'daily', time: '08:00' });
    });

    it('_fmtSchedule builds a readable label and tolerates a partial dict', () => {
        expect(_fmtSchedule(enStrings, { kind: 'Weekly', dayOfWeek: 'Friday', time: '09:00' }))
            .toBe('Every Friday at 09:00');
        expect(_fmtSchedule(enStrings, { kind: 'EveryNDays', intervalDays: 3, time: '08:00' }))
            .toBe('Every 3 days at 08:00');
        expect(() => _fmtSchedule({}, { kind: 'Daily', time: '07:00' })).not.toThrow();
    });

    it('_fmtDateTime formats or returns null', () => {
        expect(_fmtDateTime('2026-09-06T14:30:00Z')).toBe('2026-09-06 14:30');
        expect(_fmtDateTime(null)).toBeNull();
    });

    it('_previewResult fills the movie and series counts', () => {
        expect(_previewResult(enStrings, { movies: 3, series: 1 })).toBe('Preview sent: 3 movie(s), 1 series.');
        expect(_previewResult(enStrings, {})).toBe('Preview sent: 0 movie(s), 0 series.');
        expect(_previewResult(enStrings, null)).toBe('Preview sent: 0 movie(s), 0 series.');
    });
});

describe('template helpers', () => {
    const templates = [
        { id: 'newsletter', baseId: 'newsletter', custom: false },
        { id: 'weekly-recap', baseId: 'weekly-recap', custom: false },
        { id: 'newsletter__holiday', baseId: 'newsletter', custom: true }
    ];

    it('_campaignTemplateBaseId maps the campaign kind', () => {
        expect(_campaignTemplateBaseId('Newsletter')).toBe('newsletter');
        expect(_campaignTemplateBaseId('WeeklyRecap')).toBe('weekly-recap');
    });

    it('_tplOptions filters by base id and marks the base', () => {
        expect(_tplOptions(templates, 'newsletter')).toEqual([
            { value: 'newsletter', label: 'newsletter (base)' },
            { value: 'newsletter__holiday', label: 'newsletter__holiday' }
        ]);
        expect(_tplOptions(templates).length).toBe(3);
        expect(_tplOptions(null)).toEqual([]);
    });
});

describe('PLUGIN_ID', () => {
    it('is the plugin GUID', () => {
        expect(PLUGIN_ID).toBe('7a27339e-e774-4969-9755-8cd213dcc5e7');
    });
});

describe('module import', () => {
    it('exports only the pure helpers and starts nothing', () => {
        expect(typeof enotifConfig._buildPrefRow).toBe('function');
        expect(enotifConfig._bind).toBeUndefined();
        expect(enotifConfig._loadSettings).toBeUndefined();
        expect(enotifConfig._saveSettings).toBeUndefined();
        expect(enotifConfig._onShow).toBeUndefined();
    });
});
