# SchoolNotifs

Turns school announcements (shared from Viber to a private Telegram bot) and a school tracker Google Sheet into events on a Google Calendar.

Built with Azure Functions (.NET 10, isolated worker), Azure Table Storage, Telegram Bot API, an LLM (OpenAI or Anthropic, swappable) and the Google Calendar API.

## How it works

```
Viber announcement ──share──▶ Telegram bot ──webhook──▶ Azure Function
                                                         │
                                  text / image / PDF ───▶ LLM extracts events (JSON)
                                                         │
                                          similar event nearby? ──yes──▶ ask you (buttons)
                                                         │ no
                                                         ▼
                                                 Google Calendar
Google Sheet ──hourly timer / "/sync"──▶ parse grid ──▶ LLM names + matches ──▶ Google Calendar
```

## Features

- Accepts text, photos, PDFs and image documents sent to the bot.
- Extracts birthdays, school events, quizzes, exams, written works, performance tasks, deadlines and date ranges.
- Assessments get topics, pages and references in the event description, plus the announcement body (trimmed if long).
- Timed events get a real start time (1 hour default) and the time repeated in the description.
- Birthdays repeat yearly as all-day events.
- If a similar event already exists (±7 days), the bot asks before adding or updating.
- Sheet sync: reads the tracker sheet, creates events from cells (cell text goes in the description), asks before changing anything already handled, and never deletes.
- Very short replies (for example `✅ Quiz 2, Thu Oct 9`).
- LLM provider is an adapter (`ILlmClient`): OpenAI by default, Anthropic supported.

## Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools v4
- Azure CLI and an Azure subscription
- A Telegram account (to create a bot with @BotFather)
- A Google account that owns the target calendar
- An OpenAI or Anthropic API key

## Setup

### 1. Telegram bot

1. Create a bot with @BotFather and keep the token.
2. Send the bot a message, then open `https://api.telegram.org/bot<TOKEN>/getUpdates` and note `message.chat.id`. This is `TELEGRAM_CHAT_ID`; only this chat is served.
3. Pick any random string for `TELEGRAM_SECRET` (letters, digits, `_`, `-`).

### 2. Google Calendar access (service account)

1. In Google Cloud Console, create a project and enable the **Google Calendar API**.
2. Create a service account and download a JSON key.
3. In Google Calendar, share your calendar with the service account's email using **Make changes to events**.
4. Copy the calendar ID from the calendar's settings (Integrate calendar). That is `JUJU_CALENDAR_ID`.
5. Base64-encode the key file (or paste the raw JSON) for `GOOGLE_SA_JSON`. Never commit the key.

### 3. Google Sheet (optional)

The sheet must be viewable by anyone with the link. Set `SHEET_ID` (the part of the URL between `/d/` and `/edit`) and `SHEET_GIDS` (comma-separated `gid=` values of the tabs to sync).

### 4. Azure resources

Create a resource group, a storage account and a **Flex Consumption** Function App with the .NET 10 isolated runtime (Linux Consumption does not support .NET 10):

```bash
az login
az group create -n <rg> -l <region>
az storage account create -n <storageaccount> -g <rg> -l <region> --sku Standard_LRS
az functionapp create -n <appname> -g <rg> --storage-account <storageaccount> \
  --flexconsumption-location <region> --runtime dotnet-isolated --runtime-version 10
```

Table Storage is used for state (`Messages`, `Pending`, `State`, `Synced`); at this volume the cost is negligible.

### 5. App settings

Copy `local.settings.example.json` to `local.settings.json` for local runs. For Azure, set the same keys as app settings (`local.settings.json` is **not** deployed):

| Setting | Purpose |
|---|---|
| `TELEGRAM_TOKEN` | Bot token from BotFather |
| `TELEGRAM_SECRET` | Webhook secret header value |
| `TELEGRAM_CHAT_ID` | The only chat the bot responds to |
| `LLM_PROVIDER` | `openai` (default) or `anthropic` |
| `OPENAI_API_KEY`, `OPENAI_MODEL` | OpenAI credentials and model |
| `ANTHROPIC_API_KEY`, `ANTHROPIC_MODEL` | Anthropic credentials and model (default `claude-haiku-4-5-20251001`) |
| `GOOGLE_SA_JSON` | Service account key (base64 or raw JSON) |
| `JUJU_CALENDAR_ID` | Target calendar ID |
| `SHEET_ID`, `SHEET_GIDS` | Tracker sheet and tab gids |
| `CONFIRM_LOW_CONFIDENCE` | Ask before adding low-confidence events |
| `DEBUG_REPLIES` | Reply with hints when messages come from other chats |
| `TABLES_CONNECTION_STRING` | Storage connection string (falls back to `AzureWebJobsStorage`) |

```bash
az functionapp config appsettings set -n <appname> -g <rg> --settings KEY=value ...
```

### 6. Build and deploy

```bash
dotnet add package Google.Apis.Calendar.v3
dotnet build
func azure functionapp publish <appname>
```

### 7. Register the webhook

```bash
curl "https://api.telegram.org/bot<TOKEN>/setWebhook" \
  -d "url=https://<apphostname>/api/telegram" \
  -d "secret_token=<TELEGRAM_SECRET>"
```

Check it with `getWebhookInfo` (look at `last_error_message`). The host name is shown by `az functionapp show -n <appname> -g <rg> --query defaultHostName -o tsv`. Confirm the route matches the webhook function in `Functions.cs`.

## Usage

- Share a Viber message, image or PDF to the bot's Telegram chat. The bot replies with a short confirmation or asks via buttons when a similar event exists.
- Send `/sync` to sync the Google Sheet immediately. It also runs hourly and skips work if the sheet hasn't changed.
- Capturing from Viber: use the share sheet manually, or an Android automation app such as Tasker to remind you when a Viber notification arrives.

## Cost

Azure Functions Flex Consumption and Table Storage cost pennies at this volume. The main cost is LLM calls, which stay small with a compact model and one call per message (sheet sync adds one or two calls per changed run).

## Security

- `.gitignore` excludes `local.settings.json`, service account and credential files, `.env*`, publish profiles, `.azure/` and CSV exports. Only `local.settings.example.json` is meant to be committed.
- If a secret is ever committed, rotate it (BotFather `/revoke`, new API key, new service account key) and remove it from history.
- The webhook verifies the secret header and ignores chats other than `TELEGRAM_CHAT_ID`.

## Troubleshooting

- **No reply:** run `getWebhookInfo`; set `DEBUG_REPLIES=true`; check the Function App log stream.
- **Replied but nothing on the calendar:** check `JUJU_CALENDAR_ID`, that the calendar is shared with the service account with edit rights, and that the calendar is visible in your list.
- **`'T' is invalid after a single JSON value`:** the model added text after its JSON. Make sure you've deployed the version of `Support.cs` whose `StripFences` returns only the first balanced JSON value.
- **`/sync` finds nothing:** confirm the sheet is link-viewable and `SHEET_GIDS` has the right tab gids.

## Project layout

| File | Role |
|---|---|
| `Functions.cs` | Telegram webhook, extraction, confirmation flow |
| `SheetSync.cs`, `SheetParser.cs` | Hourly and manual sheet sync, grid parsing |
| `JujuCalendar.cs` | Google Calendar client and event building |
| `ILlmClient.cs`, `OpenAiLlmClient.cs`, `AnthropicLlmClient.cs` | LLM adapter |
| `Telegram.cs` | Telegram API wrapper |
| `Models.cs`, `Support.cs` | Event model and helpers |
| `Program.cs` | Dependency registration |
