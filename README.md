# Task 1 – Resilient Selenium tests (C#)

Automates three fields of the CloudQA practice form – **First Name** (text box), **Gender** (radio) and **State** (dropdown) – and keeps working when the page is changed.

## Setup

Requirements: .NET 8 SDK, Google Chrome. (Selenium Manager downloads the matching ChromeDriver automatically.)

```bash
cd ResilientFormTests
dotnet restore
```

## Run

```bash
# default URL (the CloudQA form)
dotnet run

# any other URL – this is how modified copies of the page are tested
dotnet run -- --url https://example.com/modified-form
FORM_URL=https://example.com/modified-form dotnet run

# local fixtures that simulate page changes
dotnet run -- --url ../fixtures/original.html
dotnet run -- --url ../fixtures/mutated-1-reordered-renamed.html
dotnet run -- --url ../fixtures/mutated-2-structure-placeholder-shadow.html

# options
dotnet run -- --first-name Alex --gender Female --state Kerala --headless --pause-ms 800
```

Exit code: `0` all three checks passed, `1` at least one failed, `2` browser could not start.
Each run prints which element it located for each field and why (label, score), then PASS/FAIL per field. On failure a `failure.png` screenshot is saved.

## How it stays resilient

The program never hard-codes an id, name, class, XPath or CSS selector for a field. Instead, one JavaScript call collects every `input` / `select` on the page (including inside open **shadow roots**, and inside **iframes** as a fallback) together with everything a human would use to recognise it:

* its label: `<label for>`, a wrapping `<label>`, `aria-label`, `aria-labelledby`, `title`
* its placeholder
* the text just before it (preceding sibling / text node) and in its nearest containers – covers table layouts and "plain text next to the control" forms
* for radios, the visible text of each option

Each candidate is then **scored** by meaning:

| Field | Strong signals | Weak signals / penalties |
|---|---|---|
| First Name | label/placeholder/preceding text says "first name" (also "given name", "forename"); an *exact* label beats `First Name (Spouse)` | id/name/class containing "first"+"name"; penalty for last/middle/spouse/full/user; hidden, disabled, nested-context candidates lose points |
| Gender | a *group* of radios (same `name`) whose options are Male/Female/…, near the word "Gender" | group name/id; groups with Yes/No options are rejected |
| State | `<select>` labelled "State" (or province/region) | id/name/class; a select labelled "Country" is penalised |

The best-scoring candidate above a threshold wins; ties go to the first one in DOM order and a warning is printed. Because ids, order and nesting only count as weak hints (or not at all), reordering fields, renaming attributes, changing wrappers and adding look-alike fields do not change the result. Fields that appear late (dynamic rendering) are retried for `--timeout` seconds.

Each action is **verified**, not just performed: the typed value is read back, exactly one radio in the group must be checked, and the dropdown's selected option text must equal the one requested. Radio "male" is matched by exact text so it never selects "female".

## Notes about the target page

When inspecting the CloudQA page I noticed the dropdown under the **State** label lists *countries* (India, Canada, …). The program therefore identifies the State field by its label, not by its options, and by default picks the first real option. Pass `--state <text>` to pick a specific one.

## Reliability

The program waits for the page to finish loading, reloads once if the first field is not found, and if Chrome itself closes or crashes mid-run (reported as `invalid session id`) it starts a fresh browser and retries once instead of reporting field failures. Chrome's background updater is disabled for the run.

## Continuous integration

`.github/workflows/ci.yml` builds the project and runs it headless against the three fixtures on every push, so the badge on the repo shows it works.

## Security notes

* `--url` accepts only `http(s)` URLs or a local file path; other schemes (`javascript:`, `data:` ...) are rejected. Page load has a 60 s timeout; `--timeout` is limited to 1–300 s.
* The program only types the value you pass and reads page state; it stores and sends nothing. `failure.png` may show page content, so don't commit it (it is in `.gitignore`).
* Selenium is pinned to 4.25.0. Only run it against pages you trust or own.

## Limitations (honest list)

* Label-based: a field with no label, placeholder, aria text or nearby text at all cannot be identified.
* Custom JavaScript dropdowns (div-based, non-`<select>`) and **closed** shadow roots are not supported; radios without a `name` attribute are not grouped.
* Language: matching uses English words (first name, gender, state).
* If two fields are equally good matches the first in DOM order is used (a warning is printed).
* The thresholds and weights in `Scorer` are hand-tuned, not trained.
