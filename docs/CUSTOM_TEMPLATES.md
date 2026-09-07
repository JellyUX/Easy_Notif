# Custom email templates

Easy Notif ships two built-in HTML templates, one per campaign kind:

| Base template id | Used by | Languages |
|---|---|---|
| `newsletter` | the new-media newsletter | `en`, `fr` |
| `weekly-recap` | the personalised weekly recap | `en`, `fr` |

The built-in templates are **read-only**. To change how a campaign's email looks, you clone a base
template into an editable copy and point the campaign at it. This guide lists every placeholder you
can use and the rules the editor enforces.

> Manual admin emails (the **Manual email** tab) are not templated - they send the body you type.

---

## Quick start

1. **Dashboard > Plugins > Easy Notif > Templates**.
2. Pick a base template (`newsletter` or `weekly-recap`) and a language. The editor shows it
   read-only.
3. **Clone**. Give the copy a short name (lowercase letters, digits and hyphens, e.g. `holiday`).
   You now have a custom template with id `newsletter__holiday`, in both `en` and `fr`.
4. Edit the HTML. Use **Refresh preview** to render it with sample data in the panel below.
5. **Save**. The editor validates the body first (see [Limits](#limits-and-validation)).
6. **Campaigns** tab: open the campaign, set **Template** to your custom template, **Save**.

The next newsletter / recap (scheduled run or "Run now" / "Send a preview to me") uses your copy.

Repeat step 2-5 for the other language if you want it customised too. A custom template that has no
copy for a given language falls back to its own `en` copy, then to the built-in base.

---

## Where the files live

Custom templates are plain HTML files under your Jellyfin data directory:

```
<DataPath>/Jellyfin.Plugin.EasyNotif/templates/
  newsletter__holiday-en.html
  newsletter__holiday-fr.html
```

(`<DataPath>` is your Jellyfin data directory, e.g. `/config/data` in the official Docker image.
`Jellyfin.Plugin.EasyNotif/` under it is the plugin's single data folder - deleting it is a full reset.)

You can edit these files directly on disk instead of using the editor - the plugin reads them fresh
on every send. Deleting a file (or the whole `templates/` folder) is always a safe reset. If you
edit on disk, run the same checks the editor does: keep the file under 64 KB, no `<script>`, and
only the placeholders listed below.

---

## The template language

The renderer is deliberately tiny. It only understands these constructs:

| Syntax | Meaning |
|---|---|
| `{{key}}` | Insert the value, HTML-escaped (`<`, `>`, `&`, `"`, `'`). Accented characters are kept as-is. |
| `{{{key}}}` | Insert the value **raw**, no escaping. Only use this for values you know are safe HTML. |
| `{{#if key}} ... {{/if}}` | Render the block only when `key` is *truthy*. |
| `{{#each list}} ... {{/each}}` | Repeat the block once per item in `list`. |
| `{{.}}` | Inside `{{#each}}`, the current item itself (for a list of plain strings). |
| `{{@index}}` | Inside `{{#each}}`, the zero-based position of the current item. |

**Truthy** means: not `null`, not `false`, a non-empty string, a non-empty list, or a non-zero
number. Everything else is falsy, so `{{#if hasMovies}}` and `{{#if overview}}` both work as you'd
expect.

Blocks can nest (`{{#if}}` inside `{{#each}}` and vice versa). Inside an `{{#each}}` block you can
still reach the outer values by name. An unknown placeholder renders as an empty string - but the
editor will not let you **save** a template that references one.

There are no partials, no expressions, no dotted paths (`{{a.b}}`), no `{{else}}`.

---

## Newsletter placeholders

Base id `newsletter`. Sample data is shown in the editor preview.

### Top level

| Placeholder | Type | Notes |
|---|---|---|
| `{{heading}}` | text | e.g. `Nouveautés sur <server name>` / `New on <server name>`. |
| `{{summary}}` | text | e.g. `3 ajout(s)` / `3 addition(s)`, or `Rien de neuf cette semaine` when empty. |
| `{{#if isEmpty}}` | bool | True when nothing new was added in the window. Use it for a "nothing new" block. |
| `{{#if hasMovies}}` | bool | True when there is at least one new movie. |
| `{{#if hasSeries}}` | bool | True when there is at least one series with new episodes. |
| `{{#each movies}}` | list | New movies, newest first. Fields below. |
| `{{#each series}}` | list | Series with new episodes, newest first. Fields below. |
| `{{#if unsubscribeUrl}}` / `{{unsubscribeUrl}}` | text | One-click unsubscribe link for the recipient. Absent when no public server URL is set. Keep a link to it in the footer. |

### Inside `{{#each movies}}`

| Placeholder | Type | Notes |
|---|---|---|
| `{{title}}` | text | The movie title. |
| `{{year}}` | number | Production year. May be absent - guard with `{{#if year}}`. |
| `{{genres}}` | text | Comma-separated list, e.g. `Crime, Drama`. Empty string when none. |
| `{{overview}}` | text | Synopsis. May be empty - guard with `{{#if overview}}`. |
| `{{posterUrl}}` | text | Absolute image URL, **or** `cid:poster-<id>` when posters are attached inline (no public URL). Absent when the item has no usable image. Use it in `<img src="{{posterUrl}}">`. |
| `{{detailUrl}}` | text | Deep link to the title in the web client. Absent when no public server URL. |
| `{{#if noLink}}` | bool | True exactly when `detailUrl` is absent - render the title as plain text instead of a link. |

### Inside `{{#each series}}`

| Placeholder | Type | Notes |
|---|---|---|
| `{{title}}` | text | The series title. |
| `{{episodesLabel}}` | text | e.g. `3 nouveaux épisodes` / `1 new episode`. |
| `{{seasonsLabel}}` | text | e.g. `Saison 1` / `Saisons 1, 2`, or `épisodes divers` when the season numbers are unknown. |
| `{{overview}}` | text | Series synopsis. May be empty. |
| `{{posterUrl}}` | text | Same rules as for movies. |
| `{{detailUrl}}` | text | Same rules as for movies. |
| `{{#if noLink}}` | bool | Same rule as for movies. |

---

## Weekly recap placeholders

Base id `weekly-recap`. The recap is built per recipient, so the values reflect one user's week.

### Top level

| Placeholder | Type | Notes |
|---|---|---|
| `{{greeting}}` | text | e.g. `Bonjour Alex` / `Hi Alex`, or just `Bonjour` / `Hi` when the username is unknown. |
| `{{heading}}` | text | e.g. `Ta semaine sur <server name>` / `Your week on <server name>`. |
| `{{#if quietWeek}}` | bool | True when the user watched nothing significant this week. Use it for a short "quiet week" block. |
| `{{#if hasWatched}}` | bool | The opposite of `quietWeek` - true when there is at least one entry. |
| `{{#each watched}}` | list | This week's entries, newest first. Fields below. |
| `{{yearCompleted}}` | number | Titles finished so far this calendar year. |
| `{{yearTotal}}` | number | Significant views so far this year (finished or not). |
| `{{#if partialSince}}` / `{{partialSince}}` | text | A `yyyy-MM-dd` date. Present only while the yearly total does not yet cover a full year - show a "counted since we installed Easy Notif on {{partialSince}}" note. |
| `{{#if unsubscribeUrl}}` / `{{unsubscribeUrl}}` | text | Same as the newsletter. |

### Inside `{{#each watched}}`

Each entry is a movie, a single episode, or a group of two or more episodes of the same series.

| Placeholder | Type | Notes |
|---|---|---|
| `{{#if isSeries}}` | bool | True for a grouped series entry (2+ episodes). |
| `{{#if isSingle}}` | bool | True for a movie **or** a single episode - the opposite of `isSeries`. |
| `{{title}}` | text | The movie title, the episode title, or (for a group) the series title. |
| `{{#if seriesTitle}}` / `{{seriesTitle}}` | text | The parent series title, set only for a **single episode** entry. Absent for movies and groups. |
| `{{episodeCount}}` | number | Episode count for a group; `1` otherwise. |
| `{{when}}` | text | `yyyy-MM-dd` of the most recent view in the entry. |
| `{{#if completed}}` | bool | True when at least one view in the entry counted as completed. |

A common pattern:

```html
{{#each watched}}
  <li>
    {{#if isSeries}}<strong>{{title}}</strong> - {{episodeCount}} épisodes{{/if}}
    {{#if isSingle}}{{#if seriesTitle}}{{seriesTitle}} - {{/if}}<strong>{{title}}</strong>{{/if}}
    <span>{{when}}</span>
  </li>
{{/each}}
```

---

## The plain-text part

Every email also carries a plain-text alternative (required for deliverability). That part is
**generated in code**, not from your template. Custom templates only change the HTML body; the
text version always follows the built-in layout.

---

## Email-safe HTML guidelines

Mail clients are far stricter than browsers. When you edit a template:

- Lay it out with `<table role="presentation">`, not flexbox or CSS grid.
- Put **all** CSS inline (`style="..."`). `<style>` blocks and external stylesheets are unreliable.
- Keep the content column around 600 px wide, with `max-width:100%` for small screens.
- Give every `<img>` a `width` and an `alt`, and design so the email still reads with images blocked.
- Do **not** use CSS custom properties (`var(--jf-...)`) - those only exist in the Jellyfin web UI.
- Accented characters are fine - the templates are UTF-8 and carry `<meta charset="utf-8">`.
- Keep the unsubscribe link in the footer (`{{#if unsubscribeUrl}}...{{/if}}`).

The clone you start from is already email-safe - the smallest change is to edit its text and colours
and leave the structure alone.

---

## Limits and validation

**Save** is rejected (with a specific message) when the body:

- is larger than **64 KB**;
- contains a `<script` tag (case-insensitive);
- references a placeholder that is **not in the list above** for that base template. The message
  names the offending placeholder.

You also cannot:

- edit or delete a base template;
- delete a custom template while a campaign still points at it (repoint the campaign first).

---

## Preview

**Refresh preview** renders the current editor content with fixed sample data (a couple of demo
movies and series, or a demo week) inside a sandboxed frame. It does not send anything and does not
use your real library. Use **Send a preview to me** on the Campaigns tab to get a real email with
real data at your own contact address.

---

## Resetting

- **Campaigns** tab: set **Template** back to the base id (`newsletter` / `weekly-recap`).
- **Templates** tab: select the custom template and **Delete** (both languages go).
- On disk: delete the `*.html` files, or the whole `templates/` folder.
