# Bulk Metadata Editor for Jellyfin

Find items across **all of your libraries** and edit their metadata in bulk — genres, tags, studios, parental ratings, titles, sort titles, years, release dates, overviews, provider ids and field locks — with a **preview of every change**, a full **undo history**, a **genre / tag manager**, and **CSV export / import** for spreadsheet editing.

Built for **Jellyfin 10.10.x** (tested target: 10.10.7, .NET 8).

---

## Install

1. In Jellyfin go to **Dashboard → Plugins → Repositories**, click **+** and add:
   - **Name:** `Bulk Metadata Editor`
   - **URL:** `https://raw.githubusercontent.com/justhallwest-commits/jellyfin-bulk-metadata/main/manifest.json`
2. Open **Catalog**, find **Bulk Metadata Editor** (Metadata section) and click **Install**.
3. **Restart Jellyfin.**
4. Open it from **Dashboard → Bulk Metadata Editor** in the left-hand menu (or Plugins → Bulk Metadata Editor → Settings).

**Manual install:** download the zip from [Releases](../../releases), unzip it into a folder such as `plugins/BulkMetadataEditor_1.0.0.0/` inside your Jellyfin config directory, and restart.

Only administrators can open the page or call its API.

---

## How it works

### 1 · Find items
- Search by title, original title, sort title or **file path** (e.g. `/4k/` or `1080p`), and by series name.
- Filter by **library**, **item type** (movies, series, seasons, episodes, collections, music, books…), genres, tags (has / doesn't have), studios, parental rating, year range, any field containing text, lock state, and **missing data** (no overview, no genres, no IMDb id, no poster, no backdrop…).
- Tick rows (shift-click selects a range), **Select page**, or **Select all matches** — selections survive across searches and pages.

### 2 · Edit selected
Build a list of edits that run top-to-bottom on every selected item. One-click **quick starts** cover the common jobs.

| Action | Works on | Example |
|---|---|---|
| Set to | any field | Parental rating = `PG-13`; Sort title = `Star Wars {Year}` |
| Clear | any field | Clear the tagline |
| Add / Remove values | genres, tags, studios, countries | Add tags `4K \| HDR`; remove tag `Kids` (regex optional) |
| Find & replace | text and lists | `Sci-Fi` → `Science Fiction`; regex `^The (.*)$` → `$1, The` |
| Add text at start / end | text | Append ` (Director's Cut)` |
| Change capitalization | text and lists | Title Case, Sentence case, UPPER, lower |
| Copy from another field | text and lists | Original title → Title |
| Clean up / Sort A→Z | lists and text | Trim spaces, remove duplicate genres, sort tags |
| Lock / Unlock fields | locks | Lock Genres + Tags so refreshes don't undo your work |

Values can use tokens: `{Name}`, `{Year}`, `{OriginalTitle}`, `{SeriesName}`, `{SeasonNumber:00}`, `{EpisodeNumber:00}`, `{Library}`, `{Type}`, `{FileName}`, `{Folder}` and `{value}` (the field's current value).

Options:
- **Only fill where empty** — set a value only on items that don't have one.
- **Lock edited fields** (on by default) — automatically locks each field you change so library scans and metadata refreshes won't overwrite it.

**Preview changes** shows a before/after diff for every item and flags anything invalid. **Apply** runs in the background with a progress bar and can be cancelled. The page also has a **Refresh from online providers** button for the selection (fill missing, replace metadata, replace images).

### Genres, tags & studios
Load every distinct genre, tag, studio, country, parental rating, custom rating, year or metadata language with item counts — across all libraries, the current filter, or the selection. Then **rename**, **merge** several values into one, **delete** them everywhere, or jump to the items that use a value.

### Spreadsheet (CSV)
Export the selection, the current filter or everything to CSV (choose the columns), edit in Excel / Google Sheets / LibreOffice, and import it back. Lists use `|` as the separator. Only cells that differ are written; read-only columns (Library, Type, Path…) are ignored; blank cells are ignored unless you tick *Blank cells clear the field*. You always get a full preview first.

### History & undo
Every batch (including imports and undos) is recorded with the exact before/after values. **Undo** restores the previous values — and an undo can itself be undone. The last 50 batches are kept (configurable in the plugin's settings XML via `MaxHistoryEntries`).

---

## Notes

- Changes are saved the same way Jellyfin's own metadata editor saves them, so NFO files / embedded metadata savers you have enabled are updated too.
- Editing a **series** does not change its seasons or episodes; select them too if you want them changed (filter by Series name and pick the Season / Episode types).
- Artists and people aren't library children, so they don't appear in searches.

## Building

```bash
dotnet publish Jellyfin.Plugin.BulkMetadata -c Release -o out
```

Pushing a tag like `v1.0.1` (or running the **Build & release** workflow with a version) builds the plugin, creates a GitHub release and adds the version to `manifest.json`, so Jellyfin will offer the update automatically.

## API

All endpoints are under `/BulkMetadata` and require an administrator token: `Catalog`, `Search`, `Values`, `Preview`, `Apply`, `Import/Preview`, `Import/Apply`, `Export`, `Refresh`, `Jobs/{id}`, `Jobs/Current`, `Jobs/{id}/Cancel`, `History`, `History/{id}`, `History/{id}/Undo/Preview`, `History/{id}/Undo`.

## License

GPL-3.0, like Jellyfin.
