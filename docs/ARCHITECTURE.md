# How SchoolNotifs works

A guide to the code: what every file does, how the app flows (explained simply), and how to keep it healthy.

- [1. The app in plain words](#1-the-app-in-plain-words)
- [2. The three flows](#2-the-three-flows)
- [3. File by file](#3-file-by-file)
- [4. Where data is stored](#4-where-data-is-stored)
- [5. Settings](#5-settings)
- [6. Maintaining the app](#6-maintaining-the-app)
- [7. Tests](#7-tests)
- [8. Known quirks](#8-known-quirks)

---

## 1. The app in plain words

Imagine a very organised helper who sits between your phone and the family calendar.

1. A parent gets a school announcement on Viber ("Science quiz on Friday, chapters 1 and 2!").
2. They **share it to a private Telegram chat** with the helper.
3. The helper **reads it** (it has a smart robot friend, the "LLM", who is good at reading messy text, photos and PDFs).
4. The robot finds the dates and writes them down neatly: *what*, *when*, *what to study*.
5. The helper **peeks at the calendar**. Is there already something that looks the same?
   - **No** and it is sure about the date: it puts the event on the calendar and says "✅ Added".
   - **Yes**, or it is unsure: it asks you with buttons: **Update / Add as new / Skip**.
6. Every hour, the helper also reads the **class tracker spreadsheet**. New cells become events. If a cell changes, moves or disappears, the helper **asks you first** instead of changing the calendar on its own.

The helper is polite: it never deletes or rewrites anything without a button tap, and it never answers anyone except you.

### The cast

| Character | Real thing | Job |
|---|---|---|
| The mailbox | Telegram bot | Where you send messages and tap buttons |
| The front door | Azure Function (`telegram` webhook) | Wakes up when Telegram delivers a message |
| The robot reader | LLM (OpenAI or Anthropic) | Turns text, photos and PDFs into neat event lists |
| The calendar | Google Calendar (via a service account) | Where events live |
| The notebook | Azure Table Storage | Remembers questions we asked and what we already did |
| The alarm clock | Timer function (hourly) | Triggers the spreadsheet check |

---

## 2. The three flows

### Flow A: an announcement arrives

```
You share a message/photo/PDF to the bot
        │
        ▼
TelegramWebhook (Functions.cs)
  ├─ wrong secret?           → reject (401)
  ├─ not your chat?          → ignore
  ├─ seen this update before?→ skip (Telegram sometimes repeats itself)
  ├─ text is "/sync"?        → go to Flow C
  └─ otherwise: download any photo/PDF, then hand over
        │
        ▼
AnnouncementProcessor
  1. Ask the LLM: "find the dated items" → JSON list of events
  2. For each event:
       no date?                → say "⚠️ No date"
       date in the past?       → treat as "not sure" (probably a misread year)
       ask the calendar: anything within ±7 days?
         └─ if yes, ask the LLM: "is any of these the same thing?"
       similar event found OR not sure → save a "pending question", send buttons
       otherwise                       → add to the calendar, say "✅"
  3. Send one summary message
```

### Flow B: you tap a button

```
Button tap → TelegramWebhook → ButtonHandler
  1. Is it your chat and valid data?   (else just acknowledge)
  2. "Claim" the pending question       (a double tap can't run twice)
  3. Answer the tap (best-effort; a late answer is ignored), then do what the button says:
       Add / Update / Skip / Delete / Keep
  4. If it came from the spreadsheet, remember the result in the Synced table
  5. Replace the question with the result ("✅ Added: Science Quiz, Fri Oct 9")
If the action fails, the question is un-claimed so the buttons work again.
```

### Flow C: the spreadsheet check (hourly, or `/sync`)

```
SheetPollTimer / "/sync" → SheetSync
  tabs = SHEET_GIDS if set, otherwise every visible tab (asked from the Sheets API)
  for each tab (gid):
    download CSV  →  same as last time? stop (timer only; /sync always rechecks)
    SheetParser: find the date header row, read each subject row
                 (identical neighbouring cells become one date range)
    keep only upcoming items
    compare with the Synced table:
       NEW        → LLM names it ("Math Written Work 3") → look-alike check →
                    add it, or ask if a look-alike exists
       CHANGED    → ask "Update?"         (text or end date changed)
       MOVED      → ask "Update?"         (same text, different first day)
       VANISHED   → ask "Delete from calendar?" (unless it was already deleted by hand)
    remember the sheet's fingerprint, but only if nothing failed (so failures retry)
```

---

## 3. File by file

All C# files are in the repository root. Class names are `JujuCalendar`, `SheetSync` and `SheetParser`; the files are spelled `Jujucalendar.cs`, `Sheetsync.cs` and `Sheetparser.cs`.

### Entry points (things Azure or Telegram call)

| File | What it is | Why it exists |
|---|---|---|
| `Program.cs` | Starts the app and registers the parts (HTTP, Telegram, calendar, LLM, sheet sync…). Picks the LLM provider from `LLM_PROVIDER`. Turns on Application Insights if configured. | Every .NET app needs one place that wires things together. |
| `Functions.cs` | `TelegramWebhook`: the front door. Checks the secret header, ignores other chats, dedupes repeated updates, handles `/sync`, downloads photos and PDFs, then routes to the right handler. Also holds `TelegramUpdate`, a tiny helper for reading chat ids. | Keeps "who is allowed in and where does it go" separate from "what to do with it". It contains no business rules. |
| `Sheetsync.cs` | `SheetPollTimer` (hourly trigger) and `SheetSync` (the whole spreadsheet-to-calendar process). Contains the naming and matching prompts for the LLM. | The tracker sheet is a second input source with its own rules: new, changed, moved and vanished items. |

### Doing the work

| File | What it is | Why it exists |
|---|---|---|
| `AnnouncementProcessor.cs` | Flow A. Holds the extraction prompt and the "is this the same event?" prompt. Decides: add now, or ask. | The "brain" for announcements, with no knowledge of HTTP or Telegram JSON. |
| `ButtonHandler.cs` | Flow B. Carries out Add/Update/Skip/Delete/Keep, including sheet bookkeeping and recovery when an action fails. | The one place that changes the calendar in response to a button. |
| `Sheetparser.cs` | `SheetParser` (CSV reader and grid reader) and `SheetItem` (one calendar-worthy cell, with its identity `Key`, row key `Rk` and change fingerprint `Hash`). | Turns a messy spreadsheet into a clean list of items. Pure logic, easy to test. |
| `CalendarEventBuilder.cs` | Turns an `ExtractedEvent` into a Google Calendar event: all-day vs timed, multi-day ranges, yearly birthdays, the description text. | All "what should the calendar entry look like" rules in one place. |
| `EventText.cs` | Short human-readable text: "Fri Oct 9 – Mon Oct 12", and one-line summaries of existing events for the LLM. | Shared by Telegram messages and LLM prompts, so both show dates the same way. |

### Talking to the outside world

| File | What it is | Why it exists |
|---|---|---|
| `SheetTabs.cs` | `ISheetTabLister` and `GoogleSheetTabLister`: asks the Google Sheets API which tabs the spreadsheet has (skipping hidden ones), so a new month needs no setting change. Turns the usual failures (403, 404, 400) into plain-language messages. | The app used to need `SHEET_GIDS` updated every month. |
| `GoogleCredentials.cs` | Builds the service-account sign-in from `GOOGLE_SA_JSON` for a given scope. | Calendar and Sheets use the same account, so the parsing lives in one place. |
| `Jujucalendar.cs` | `JujuCalendar`: the Google Calendar client (list, get, insert, update, delete). Also small pure helpers: `StartDay`, `EndDay`, `SheetText`, `IsGone`. | The only file that talks to Google. Swap or fake it without touching the rest. |
| `Telegram.cs` | `TelegramApi`: send, edit, answer button taps, download files. Builds the inline button keyboards and reads the button data back. | The only file that talks to Telegram. |
| `ILlmClient.cs` | The `ILlmClient` interface, the input parts (`TextPart`, `ImagePart`, `PdfPart`), and `HttpLlmClient`, the shared base for providers (POST JSON, check errors, parse reply). | Defines the one LLM surface the app uses. |
| `OpenAiLlmClient.cs` | OpenAI adapter (Chat Completions). | Translates the neutral parts to OpenAI's format. |
| `AnthropicLlmClient.cs` | Anthropic adapter (Messages API). | Same, for Anthropic. Adding a provider means adding one more file like this. |

### Data and shared building blocks

| File | What it is | Why it exists |
|---|---|---|
| `Models.cs` | `ExtractedEvent` (the neutral event), `Extraction.Parse` (tolerant JSON reader for LLM answers), `EventTypes`, `Confidence`. | The shared language of the app. The parser forgives sloppy LLM output (numbers instead of strings, odd casing). |
| `Stores.cs` | Table Storage access: `MessageStore` (seen updates), `PendingStore` (questions waiting for a tap, with claim and release), `SyncStore` (what we did for each sheet item), `SheetStateStore` (last processed sheet fingerprint). | Keeps storage details out of the logic. |
| `Constants.cs` | Names that must match everywhere: setting names, table and column names, status values, button actions, date formats, MIME types, Manila time. | No magic strings or numbers scattered in code. |
| `Support.cs` | Small helpers: `Env` (settings), `Db` (table access and test hook), `TextUtil` (flatten, truncate), `DateText` (parse and format dates), `LlmUtil` (clean up LLM output to the first JSON value). | The same few helpers were needed in many places. |

### Project files

| File | Purpose |
|---|---|
| `SchoolAssistant.csproj` | Build settings and packages. Keeps the test folder out of the deployed package. |
| `host.json` | Azure Functions host settings. |
| `Properties/launchSettings.json` | Local debug settings. |
| `.github/workflows/deploy.yml` | CI: run tests, publish, deploy from `main`. |
| `README.md` | Setup, deployment and usage. |
| `CLAUDE.md` | Notes for the AI assistant that works on this repo. |

### Tests (`SchoolAssistant.Tests/`)

| File | Covers |
|---|---|
| `Infrastructure.cs` | The fakes: in-memory tables, stubbed HTTP, scripted LLM, fake Google Calendar server, and `Harness`, the shared setup. |
| `HelperTests.cs` | `TextUtil`, `DateText`, `LlmUtil`, `Env`, `ManilaTime`, `Db`. |
| `ModelTests.cs` | `ExtractedEvent` and `Extraction.Parse`. |
| `SheetTabListerTests.cs` | Tab discovery and its error messages. |
| `SheetParserTests.cs` | CSV, grid reading, year guessing, `SheetItem` keys and hashes. |
| `CalendarTextTests.cs` | `EventText`, `CalendarEventBuilder`, `JujuCalendar` static helpers. |
| `TelegramApiTests.cs` | Telegram calls, keyboards, callback data. |
| `StoreTests.cs` | All four stores, including the double-tap race. |
| `LlmClientTests.cs` | OpenAI and Anthropic request and response shapes. |
| `JujuCalendarTests.cs` | Calendar calls against the fake Google server. |
| `AnnouncementProcessorTests.cs` | Flow A. |
| `ButtonHandlerTests.cs` | Flow B. |
| `SheetSyncTests.cs` | Flow C and the hourly timer. |
| `WebhookTests.cs` | Secret check, chat filter, attachments, error replies. |
| `coverage.runsettings` | Coverage settings. |

---

## 4. Where data is stored

Azure Table Storage holds four small tables. **Their names, column names and key formats are stored data**: renaming them in code orphans existing rows.

| Table | One row per | Holds | Used by |
|---|---|---|---|
| `Messages` | Telegram update | The text and the raw LLM answer. Its existence is how repeats are detected. | `MessageStore` |
| `Pending` | Question with buttons | The proposed event, the matching calendar event id, status (`pending`/`done`), and for sheet questions the sheet details. | `PendingStore` |
| `Synced` | Sheet item (tab + subject + first day) | The calendar event id, status (`synced`, `skipped`, `removed`, `kept`, `moved`), content hash, "already asked" hashes. | `SyncStore` |
| `State` | Sheet tab | Fingerprint of the last fully processed CSV, so unchanged sheets are skipped. | `SheetStateStore` |

How a sheet item is recognised:

- **Key**: `tab|SUBJECT|first-day`. Editing the text keeps the same key. Moving the first day creates a new one.
- **Hash**: text + end date. A different hash means "changed".
- **Row key (Rk)**: a hash of the Key, used as the table row key.

---

## 5. Settings

See the table in the README for the full list. In short: Telegram (`TELEGRAM_TOKEN`, `TELEGRAM_SECRET`, `TELEGRAM_CHAT_ID`), LLM (`LLM_PROVIDER` plus that provider's key and model), Google (`GOOGLE_SA_JSON`, `JUJU_CALENDAR_ID`), sheet (`SHEET_ID`; `SHEET_GIDS` is an optional override of which tabs to sync), behaviour (`CONFIRM_LOW_CONFIDENCE` defaults to true, `DEBUG_REPLIES` defaults to false), and storage (`TABLES_CONNECTION_STRING`, falling back to `AzureWebJobsStorage`).

Rule: read settings through `Env` using the names in `Constants.cs` (`Settings`).

---

## 6. Maintaining the app

### Everyday commands

```bash
dotnet build
dotnet test SchoolAssistant.Tests
# coverage report (cobertura XML in ./TestResults)
dotnet test SchoolAssistant.Tests --settings SchoolAssistant.Tests/coverage.runsettings --collect:"XPlat Code Coverage"
```

Before every commit: build and test must pass. CI runs the same tests before publishing, and deploys only from `main`.

### Where do I change…?

| I want to… | Change this | Also |
|---|---|---|
| Change what the robot extracts from announcements | `ExtractPrompt` in `AnnouncementProcessor.cs` | If you add a JSON field, add it to `ExtractedEvent` and `Extraction.Parse`, then to `CalendarEventBuilder` if it should appear on the calendar. Add a test. |
| Change how sheet items are named or matched | `TitlePrompt`, `MatchPrompt` in `Sheetsync.cs` | Update `SheetSyncTests` (the fake LLM there answers by prompt text). |
| Add an event type | `EventTypes` in `Models.cs`; the type list in the prompts; `Types` in `Sheetsync.cs` if sheets can produce it; `IsAssessment` if it should carry the announcement text | Test in `ModelTests` / `CalendarTextTests`. |
| Change how an event looks on the calendar | `CalendarEventBuilder.cs` | `CalendarTextTests`. |
| Change the wording or date style of messages | `EventText.cs`, the message strings in the handlers, `Formats` in `Constants.cs` | Tests assert exact text, so update them. |
| Add or rename a button | `KeyboardKind` and `Keyboard` in `Telegram.cs`; the action in `CallbackAction`; the case in `ButtonHandler.ApplyAsync` | `TelegramApiTests`, `ButtonHandlerTests`. |
| Add an LLM provider | New file like `OpenAiLlmClient.cs` inheriting `HttpLlmClient`; a name in `LlmProviders`; a case in `Program.cs`; settings in `Settings` | A request/response test in `LlmClientTests`. |
| Support a different sheet layout | `SheetParser.Parse` and helpers in `Sheetparser.cs` | `SheetParserTests` with a small CSV sample. |
| Add a setting | A name in `Settings` (`Constants.cs`), read it with `Env`, document it in the README | |
| Change timing (hourly sync, ±7 days, sizes, limits) | Named constants at the top of the relevant class | Update tests that depend on them. |
| Remove a feature | Delete the code, its tests and its README line | Check `Constants.cs` for names that are now unused. |

### House rules

1. **No magic numbers or strings.** Give them a name (`Constants.cs` for shared ones, a `const` in the class for local ones), with a comment on the unit or reason.
2. **One job per file.** Webhook routes. Processor decides. Handler acts. Stores save. Clients talk to outside services. Do not mix them.
3. **Do not repeat yourself.** Dates go through `DateText`, LLM JSON through `LlmUtil.ParseJson`, settings through `Env`.
4. **Calendar dates are Manila time** (`ManilaTime`). All-day events end the day *after* their last day (`CalendarConventions.AllDayEndOffsetDays`).
5. **Never change stored names casually**: table names, column names, partition keys, `PendingKind`/`SyncStatus` values, the key and hash formats and lengths in `SheetItem`.
6. **Ask before changing the calendar.** Anything that edits or deletes an existing event must go through a button.
7. **Keep it simple.** If something is not used and not needed yet, remove it.

### Troubleshooting

| Symptom | Check |
|---|---|
| Bot does not reply | `getWebhookInfo` on Telegram; secret matches; chat id matches; set `DEBUG_REPLIES=true` for a hint. |
| Replies but nothing on the calendar | `JUJU_CALENDAR_ID`; calendar shared with the service account with edit rights. |
| "Couldn't read the AI result" | The raw model answer is saved in the `Messages` table (`ResultJson`) for that update id. |
| `/sync` finds nothing | Sheet must be viewable by anyone with the link. Tabs with no upcoming dates are skipped silently. |
| `/sync` says "Google rejected sheet … tab … (400)" | `SHEET_ID` or a gid in `SHEET_GIDS` is wrong; the message names both. |
| `/sync` says "Can't list the tabs … (403)" | Enable the Google Sheets API and share the sheet with the service account as Viewer, or set `SHEET_GIDS`. |
| Sheet keeps asking the same question | Look at the `Synced` row for that item (`Hash`, `AskedHash`, `Status`). |
| A button says "Already handled" or "Expired" | The question was already answered, or the `Pending` row is gone. |

### Changing code safely

1. Write or adjust a test first when the change is behaviour (the fakes in `Infrastructure.cs` make this quick).
2. Make the change.
3. `dotnet test SchoolAssistant.Tests`; coverage should stay near 100% for app code. Untested code is a sign something is missing.
4. If you touched anything stored (see rule 5), plan for existing rows.

---

## 7. Tests

The tests never use the network or Azure. Instead they use stand-ins from `SchoolAssistant.Tests/Infrastructure.cs`:

- `FakeTable`: an in-memory Table Storage, including concurrency checks (ETags).
- `StubHandler` / `StubHttpFactory`: pretend web servers that record what was sent.
- `FakeCalendarServer`: a tiny Google Calendar that a real `CalendarService` talks to.
- `FakeLlm`: answers with whatever a test scripts.
- `Harness`: wires all of the above together, and exposes helpers like `Sent`, `Edited` and `ProposalIdOfSent`.

Test classes that use settings or tables inherit `AppTestBase` (via `Harness`). It sets safe environment values and restores them afterwards. Tests run one at a time because settings are process-wide.

Only two things are excluded from coverage: `Program.cs` (needs a live host) and the real Azure table factory in `Support.cs` (needs Azure).

---

## 8. Known quirks

- A manual `/sync` that only sends questions (nothing added) also replies "📊 Sheet is up to date.". This is harmless but can read oddly. A test pins the behaviour for timer runs.
- Answering a button tap (the spinner) is best-effort. Telegram refuses it if the tap is more than a few seconds old, for example after a cold start. That is logged, and the tap's real action still runs.
- If Telegram itself is down, a manual `/sync` that has something to report will fail when sending the summary.
