import { describe, it, expect } from 'vitest';
import enotifConfig from '../src/Jellyfin.Plugin.EasyNotif/Web/config.js';

import enStrings from '../src/Jellyfin.Plugin.EasyNotif/Web/strings/en.json';
import frStrings from '../src/Jellyfin.Plugin.EasyNotif/Web/strings/fr.json';

const {
    _escHtml, _pickLocale, _t, _secretHint, _buildPrefRow, _buildStatus, _incidentBanner,
    _manualBody, _manualRecipients, _validateManual, _manualPayload, _validateAnnouncement, _announcementView, _previewSrcdoc, _manualResult, _manualBatchView, _fmtBytes,
    _filterLogLines,
    _scheduleFieldsForKind, _validateSchedule, _scheduleFromForm, _fmtSchedule, _fmtDateTime,
    _previewResult, _tplOptions, _campaignTemplateBaseId, _publicUrlWarning,
    PLUGIN_ID
} = enotifConfig;

describe('_escHtml', () => {
    it('escapes HTML special characters', () => {
        expect(_escHtml('<b>Tom & "Jerry"</b>')).toBe('&lt;b&gt;Tom &amp; &quot;Jerry&quot;&lt;/b&gt;');
    });

    it('returns an empty string for nullish input', () => {
        expect(_escHtml("a'b")).toBe('a&#39;b');
        expect(_escHtml(null)).toBe('');
        expect(_escHtml(undefined)).toBe('');
    });
});

describe('_pickLocale', () => {
    it('follows the Jellyfin language over the browser locale', () => {
        expect(_pickLocale('fr', 'en-US')).toBe('fr');
        expect(_pickLocale('fr-FR', null)).toBe('fr');
    });

    it('falls back to the browser locale', () => {
        expect(_pickLocale(null, 'FR-ca')).toBe('fr');
        expect(_pickLocale('', 'fr')).toBe('fr');
    });

    it('defaults to en for anything else', () => {
        expect(_pickLocale('en-US', 'en')).toBe('en');
        expect(_pickLocale('de', 'de-DE')).toBe('en');
        expect(_pickLocale(undefined, undefined)).toBe('en');
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

describe('_publicUrlWarning', () => {
    it('warns that a blank URL leaves emails without any unsubscribe link', () => {
        expect(_publicUrlWarning('')).toBe('settings.publicUrl.warnNone');
        expect(_publicUrlWarning('   ')).toBe('settings.publicUrl.warnNone');
        expect(_publicUrlWarning(null)).toBe('settings.publicUrl.warnNone');
    });

    it('warns that an http URL gets no one-click unsubscribe button', () => {
        expect(_publicUrlWarning('http://localhost:8099')).toBe('settings.publicUrl.warnHttp');
        expect(_publicUrlWarning(' HTTP://media.example.org ')).toBe('settings.publicUrl.warnHttp');
    });

    it('stays silent for an https URL', () => {
        expect(_publicUrlWarning('https://media.example.org')).toBeNull();
    });

    it('has both warnings translated', () => {
        ['settings.publicUrl.warnNone', 'settings.publicUrl.warnHttp'].forEach((key) => {
            expect(enStrings[key]).toBeTruthy();
            expect(frStrings[key]).toBeTruthy();
        });
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

    it('shows the opt-out categories and the followed series count as read-only cells', () => {
        const html = _buildPrefRow({ 'common.yes': 'Yes', 'common.no': 'No' }, {
            userId: 'u-4',
            userName: 'Carol',
            maskedEmail: 'c***l@example.org',
            hasEmail: true,
            news: true,
            recap: true,
            announcements: false,
            episodes: true,
            followedSeries: 2,
            updatedAt: null
        });

        expect(html).toContain('<td>No</td><td>Yes</td><td>2</td>');
        expect(html.match(/type="checkbox"/g)).toHaveLength(2);
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

describe('_incidentBanner', () => {
    const dict = enStrings;

    it('counts the incidents of the last 24 hours when the server raises the banner', () => {
        const recent = new Date(Date.now() - 3600 * 1000).toISOString();
        const old = new Date(Date.now() - 48 * 3600 * 1000).toISOString();

        const text = _incidentBanner(dict, { banner: true, incidents: [{ ts: recent }, { ts: recent }, { ts: old }] });

        expect(text).toBe('2 incident(s) in the last 24 hours. Open the Status tab.');
    });

    it('is empty without the banner flag or a payload', () => {
        expect(_incidentBanner(dict, { banner: false, incidents: [{ ts: new Date().toISOString() }] })).toBe('');
        expect(_incidentBanner(dict, null)).toBe('');
    });
});

describe('_buildStatus', () => {
    const dict = enStrings;

    it('shows no warning class for a nominal quota', () => {
        const el = _buildStatus(dict, {
            configured: true,
            fileTransformation: true,
            quota: { last30d: 3, dailyToday: 1, monthlyLimit: 3000, dailyLimit: 100, warn80: false, over: false },
            lastWebhookUtc: null,
            recent: []
        });

        expect(el.querySelector('.enotif-warn')).toBeNull();
        expect(el.querySelector('.enotif-kv .enotif-pill-ok').textContent).toBe(dict['status.transport.on']);
        expect(el.textContent).toContain('3 / 3000');
        expect(el.textContent).toContain('1 / 100');
    });

    it('renders the campaigns table and flags a missing FileTransformation', () => {
        const el = _buildStatus(dict, {
            configured: true,
            fileTransformation: false,
            quota: {},
            lastWebhookUtc: null,
            recent: [],
            campaigns: [{ id: 'newsletter', enabled: true, nextRunLocal: '2026-09-14 09:00', lastSentLocal: null }]
        });

        expect(el.querySelector('.enotif-warn')).not.toBeNull();
        expect(el.textContent).toContain('newsletter');
        expect(el.textContent).toContain('2026-09-14 09:00');
    });

    it('flags a paused next newsletter run in the campaigns table', () => {
        const el = _buildStatus(dict, {
            configured: true,
            fileTransformation: true,
            quota: {},
            lastWebhookUtc: null,
            recent: [],
            campaigns: [
                { id: 'newsletter', enabled: true, nextRunLocal: '2026-11-02 09:00', pauseNextRun: true },
                { id: 'weekly-recap', enabled: true, nextRunLocal: '2026-11-03 08:00', pauseNextRun: false }
            ]
        });

        const flagged = el.querySelectorAll('td.enotif-warn');
        expect(flagged.length).toBe(1);
        expect(flagged[0].textContent).toBe('2026-11-02 09:00 (' + dict['status.pausedNextRun'] + ')');
        expect(el.textContent).toContain('2026-11-03 08:00');
    });

    it('reports orphaned series subscriptions only when there are some', () => {
        const base = { configured: true, fileTransformation: true, quota: {}, lastWebhookUtc: null, recent: [] };

        expect(_buildStatus(dict, { ...base, orphanedSeriesSubscriptions: 0 }).querySelector('p.enotif-warn')).toBeNull();
        const note = _buildStatus(dict, { ...base, orphanedSeriesSubscriptions: 3 }).querySelector('p.enotif-warn');
        expect(note.textContent).toBe(dict['status.orphanedSeries'].replace('{n}', '3'));
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

    it('shows the provider suspension and the no-daily-limit wording', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: { last30d: 40, dailyToday: 12, monthlyLimit: 3000, dailyLimit: 0, warn80: false, over: true, suspendedUntilUtc: '2026-06-16T00:00:00Z' },
            lastWebhookUtc: null,
            recent: []
        });

        expect(el.querySelector('p.enotif-warn').textContent).toContain('2026-06-16 00:00');
        expect(el.textContent).toContain('12 (no daily limit)');
    });

    it('shows the campaign reserve and where the series emails stop', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: { last30d: 3, dailyToday: 1, monthlyLimit: 3000, dailyLimit: 100, warn80: false, over: false, reserve: 40 },
            lastWebhookUtc: null,
            recent: []
        });

        expect(el.textContent).toContain('40 of today');
        expect(el.textContent).toContain('series emails stop at 60');
    });

    it('flags a drain that lost a batch and shows each drain', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: {},
            lastWebhookUtc: null,
            recent: [],
            drains: {
                newItems: { lastFlushUtc: '2026-10-09T08:00:00Z', lostBatches: 1, lostItems: 4, dropped: 0, depth: 0 },
                playback: { lastFlushUtc: null, lostBatches: 0, lostItems: 0, dropped: 0, depth: 2 },
                episodes: { lastFlushUtc: null, lostBatches: 0, lostItems: 0, dropped: 0, depth: 0 }
            }
        });

        const rows = el.querySelectorAll('tbody tr');
        expect(rows.length).toBe(3);
        expect(rows[0].className).toBe('enotif-warn');
        expect(rows[0].textContent).toContain('1 batch(es), 4 item(s)');
        expect(rows[0].textContent).toContain('2026-10-09 08:00');
        expect(rows[1].className).toBe('');
        expect(rows[1].textContent).toContain(dict['status.drainNoFlush']);
    });

    it('shows a campaign error as text, never markup', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: {},
            lastWebhookUtc: null,
            recent: [],
            campaigns: [
                { id: 'newsletter', enabled: true, lastError: '<b>IOException</b>: disk full', lastErrorUtc: '2026-10-09T08:00:00Z' },
                { id: 'weekly-recap', enabled: true, lastError: null }
            ]
        });

        const cells = el.querySelectorAll('tbody tr td:last-child');
        expect(cells[0].className).toBe('enotif-warn');
        expect(cells[0].textContent).toBe('2026-10-09 08:00 UTC <b>IOException</b>: disk full');
        expect(cells[1].textContent).toBe('-');
        expect(el.innerHTML).not.toContain('<b>');
    });

    it('lists the incidents with their label, as text', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: {},
            lastWebhookUtc: null,
            recent: [],
            alertEmailSet: true,
            lastDigestUtc: '2026-10-09T08:15:00Z',
            incidents: [
                { ts: '2026-10-09T08:00:00Z', type: 'email.failed', detail: 'error=<i>API key is invalid</i>' },
                { ts: '2026-10-09T07:00:00Z', type: 'future.type', detail: null }
            ]
        });

        const rows = Array.from(el.querySelectorAll('tbody tr'));
        expect(rows[0].textContent).toContain('Send refused or failed');
        expect(rows[0].textContent).toContain('error=<i>API key is invalid</i>');
        expect(rows[1].textContent).toContain('future.type');
        expect(el.innerHTML).not.toContain('<i>');
        expect(el.textContent).toContain('last digest sent on 2026-10-09 08:15 UTC');
    });

    it('says when no alert address is set', () => {
        const el = _buildStatus(dict, { configured: true, quota: {}, lastWebhookUtc: null, recent: [], alertEmailSet: false });

        expect(el.textContent).toContain(dict['status.alertOff']);
    });

    it('says no webhook when lastWebhookUtc is null', () => {
        const el = _buildStatus(dict, { configured: true, quota: {}, lastWebhookUtc: null, recent: [] });

        expect(el.textContent).toContain(dict['status.kv.webhookNone']);
    });

    it('splits the tab into titled sections, recent sends included', () => {
        const el = _buildStatus(dict, {
            configured: false,
            fileTransformation: true,
            quota: {},
            lastWebhookUtc: null,
            alertEmailSet: true,
            campaigns: [{ id: 'newsletter', enabled: false }],
            drains: { newItems: { lastFlushUtc: null, lostBatches: 0, lostItems: 0, dropped: 0, depth: 3 } },
            recent: [{ ts: '2026-09-04T10:00:00Z', context: 'test', toMasked: 'a***e@example.com', status: 'sent' }]
        });

        expect(Array.from(el.querySelectorAll('section > h3')).map((h) => h.textContent)).toEqual([
            dict['status.section.transport'], dict['status.campaigns'], dict['status.drains'],
            dict['status.section.alerts'], dict['status.section.recent']
        ]);
        expect(el.querySelector('.enotif-kv .enotif-pill-err').textContent).toBe(dict['status.transport.off']);
        expect(el.textContent).toContain(dict['status.noIncidents']);
        expect(el.querySelectorAll('.enotif-table-scroll > table.enotif-status-table').length).toBe(3);
    });

    it('marks states with pills and right-aligns the counters', () => {
        const el = _buildStatus(dict, {
            configured: true,
            quota: {},
            lastWebhookUtc: null,
            campaigns: [{ id: 'newsletter', enabled: true }, { id: 'weekly-recap', enabled: false }],
            drains: { playback: { lastFlushUtc: null, lostBatches: 0, lostItems: 0, dropped: 1, depth: 2 } },
            recent: [
                { ts: '2026-09-04', context: 'newsletter', toMasked: 'x', status: 'sent' },
                { ts: '2026-09-04', context: 'test', toMasked: 'y', status: 'failed' },
                { ts: '2026-09-04', context: 'test', toMasked: 'z', status: 'deduplicated' }
            ]
        });

        const campaignPills = Array.from(el.querySelectorAll('section:nth-of-type(2) .enotif-pill')).map((p) => p.className);
        expect(campaignPills).toEqual(['enotif-pill enotif-pill-ok', 'enotif-pill enotif-pill-off']);
        const drainRow = el.querySelector('section:nth-of-type(3) tbody tr');
        expect(drainRow.className).toBe('enotif-warn');
        expect(Array.from(drainRow.querySelectorAll('td.enotif-num')).map((td) => td.textContent)).toEqual(['1', '2']);
        const sendPills = Array.from(el.querySelectorAll('section:last-of-type .enotif-pill')).map((p) => p.className.split(' ')[1]);
        expect(sendPills).toEqual(['enotif-pill-ok', 'enotif-pill-err', 'enotif-pill-off']);
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

    it('_validateManual refuses a script in an HTML body only', () => {
        expect(_validateManual({ subject: 'Hi', body: '<p>x</p><SCRIPT>1</SCRIPT>', bodyMode: 'html', recipientMode: 'all' })).toBe('manual.error.script');
        expect(_validateManual({ subject: 'Hi', body: 'about <script> tags', bodyMode: 'text', recipientMode: 'all' })).toBeNull();
    });

    it('_manualPayload carries the layout choice and the audience', () => {
        const all = _manualPayload({ subject: 'Hi', body: '<p>x</p>', bodyMode: 'html', wrap: true, lang: 'en', mode: 'send', recipientMode: 'all' });
        expect(all).toEqual({ subject: 'Hi', html: '<p>x</p>', text: null, wrap: true, lang: 'en', recipientMode: 'all', recipientUserIds: null });

        const selected = _manualPayload({ subject: 'Hi', body: 'x', bodyMode: 'text', wrap: false, lang: 'xx', mode: 'send', recipientMode: 'selected', checkedIds: ['a'] });
        expect(selected).toMatchObject({ text: 'x', html: null, wrap: false, lang: 'fr', recipientMode: 'selected', recipientUserIds: ['a'] });

        const test = _manualPayload({ subject: 'Hi', body: 'x', bodyMode: 'text', wrap: true, mode: 'test', testAddress: 'me@example.org' });
        expect(test).toMatchObject({ recipientMode: 'test', testAddress: 'me@example.org' });
    });

    it('_validateAnnouncement needs a date and refuses attachments', () => {
        const base = { subject: 'Hi', body: 'x', bodyMode: 'text', mode: 'send', recipientMode: 'all', attachmentBytes: 0 };
        expect(_validateAnnouncement({ ...base, sendAt: '2026-10-10T09:00' })).toBeNull();
        expect(_validateAnnouncement({ ...base, sendAt: '' })).toBe('manual.error.sendAt');
        expect(_validateAnnouncement({ ...base, sendAt: '2026-10-10T09:00', attachmentBytes: 10 })).toBe('manual.error.scheduleAttachments');
        expect(_validateAnnouncement({ ...base, subject: '', sendAt: '2026-10-10T09:00' })).toBe('manual.error.subject');
    });

    it('_announcementView lists announcements as text, with Cancel on pending ones only', () => {
        const el = _announcementView(enStrings, [
            { id: 'a1', subject: '<img src=x onerror=alert(1)>', sendAtLocal: '2026-10-10 09:00', recipientMode: 'all', status: 'pending' },
            { id: 'a2', subject: 'Done', sendAtLocal: '2026-10-01 09:00', recipientMode: 'selected', recipientCount: 2, status: 'sent', sent: 2, failed: 0, skippedNoEmail: 1 }
        ]);
        expect(el.querySelector('img')).toBeNull();
        expect(el.textContent).toContain('<img src=x onerror=alert(1)>');
        const cancels = el.querySelectorAll('[data-announcement-id]');
        expect(cancels.length).toBe(1);
        expect(cancels[0].getAttribute('data-announcement-id')).toBe('a1');
        expect(el.textContent).toContain('2 selected users');
        expect(el.textContent).toContain('2 sent, 0 failed, 1 without an address.');
    });

    it('_announcementView shows an empty state', () => {
        expect(_announcementView(frStrings, []).textContent).toBe('Aucune annonce programmée.');
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

    it('_manualBatchView shows a progress bar while the lot runs', () => {
        const el = _manualBatchView(enStrings, { id: 'x', status: 'running', total: 4, processed: 1, details: [] });
        const bar = el.querySelector('progress.enotif-progress');
        expect(bar).not.toBeNull();
        expect(bar.max).toBe(4);
        expect(bar.value).toBe(1);
        expect(el.textContent).toContain('1 / 4');
    });

    it('_manualBatchView shows the summary, and the already-sent count, once done', () => {
        const el = _manualBatchView(frStrings, {
            id: 'x', status: 'done', total: 3, processed: 3, sent: 1, failed: 0, deduplicated: 2, skippedNoEmail: 0,
            details: [{ maskedTo: 'a***e@example.org', status: 'sent' }]
        });
        expect(el.querySelector('progress')).toBeNull();
        expect(el.textContent).toContain('1 envoyés');
        expect(el.textContent).toContain('2 déjà envoyés');
        expect(el.textContent).toContain('a***e@example.org - sent');
    });

    it('_manualBatchView renders a hostile maskedTo as text, not markup', () => {
        const el = _manualBatchView(enStrings, {
            status: 'done', sent: 0, failed: 1, details: [{ maskedTo: '<img src=x onerror=alert(1)>', status: 'failed' }]
        });
        expect(el.innerHTML).not.toContain('<img');
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
        expect(_previewResult(enStrings, { movies: 0 }, 'WeeklyRecap')).toBe('Preview sent, filled with your own viewing.');
        expect(_previewResult(frStrings, {}, 'WeeklyRecap')).toBe('Aperçu envoyé, rempli avec vos propres lectures.');
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
