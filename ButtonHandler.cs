using System.Text.Json;
using Azure;
using Microsoft.Extensions.Logging;

namespace SchoolAssistant;

/// <summary>Carries out a proposal when its button is tapped in Telegram.</summary>
public class ButtonHandler(TelegramApi telegram, JujuCalendar calendar, ILogger<ButtonHandler> log)
{
    const string ExpiredNotice = "Expired";
    const string AlreadyHandledNotice = "Already handled";

    /// <param name="callback">Telegram's callback_query object.</param>
    public async Task HandleAsync(JsonElement callback)
    {
        var callbackId = callback.GetProperty("id").GetString()!;
        var message = callback.GetProperty("message");
        var chatId = TelegramUpdate.ChatOf(message);
        var messageId = message.GetProperty("message_id").GetInt64();

        var data = callback.TryGetProperty("data", out var dataElement) ? dataElement.GetString() ?? "" : "";
        if (chatId != Env.TelegramChatId || !TelegramApi.TryParseCallback(data, out var action, out var proposalId))
        {
            await telegram.AnswerCallbackAsync(callbackId);
            return;
        }

        var (outcome, proposal) = await PendingStore.ClaimAsync(proposalId);
        if (proposal == null)
        {
            await telegram.AnswerCallbackAsync(callbackId, outcome == ClaimOutcome.Expired ? ExpiredNotice : AlreadyHandledNotice);
            return;
        }
        await telegram.AnswerCallbackAsync(callbackId);

        string result;
        try { result = await ApplyAsync(action, proposal); }
        catch
        {
            await ReleaseClaimAsync(proposalId);
            throw;
        }

        // A moved item: its old row is done whichever way the user answered
        if (proposal.FromSheet && proposal.OldRk.Length > 0)
            await SyncStore.MarkAsync(proposal.Gid, proposal.OldRk, SyncStatus.Moved);

        await telegram.EditAsync(chatId, messageId, result);
    }

    /// <summary>Does what the button asked and returns the text that replaces the question.</summary>
    async Task<string> ApplyAsync(string action, Proposal proposal)
    {
        var extracted = proposal.Event;
        var title = extracted.Title;

        switch (action)
        {
            case CallbackAction.Delete when proposal.RemovedFromSheet:
                await calendar.DeleteAsync(proposal.MatchId);
                await SyncStore.MarkAsync(proposal.Gid, proposal.Rk, SyncStatus.Removed);
                return $"🗑 Deleted: {title}";

            case CallbackAction.Skip when proposal.RemovedFromSheet:
                await SyncStore.MarkAsync(proposal.Gid, proposal.Rk, SyncStatus.Kept);
                return $"📌 Kept: {title}";

            case CallbackAction.Skip:
                // A skipped change keeps its link to the existing event; a skipped new item has none
                if (proposal.FromSheet)
                    await RecordSheetItemAsync(proposal,
                        proposal.ChangedOnSheet ? proposal.MatchId : "",
                        proposal.ChangedOnSheet ? SyncStatus.Synced : SyncStatus.Skipped);
                return $"⏭ Skipped: {title}";

            case CallbackAction.Update when proposal.MatchId.Length > 0:
                var updatedId = await UpdateOrInsertAsync(proposal.MatchId, extracted, keepPreviousDetails: !proposal.ChangedOnSheet);
                if (proposal.FromSheet) await RecordSheetItemAsync(proposal, updatedId, SyncStatus.Synced);
                return $"✏️ Updated: {title}, {EventText.When(extracted)}";

            default: // CallbackAction.AddNew
                var created = await calendar.InsertAsync(CalendarEventBuilder.Build(extracted));
                if (proposal.FromSheet) await RecordSheetItemAsync(proposal, created.Id, SyncStatus.Synced);
                return $"✅ Added: {title}, {EventText.When(extracted)}";
        }
    }

    // If the calendar event was deleted in the meantime, create it again instead of failing.
    async Task<string> UpdateOrInsertAsync(string eventId, ExtractedEvent extracted, bool keepPreviousDetails)
    {
        var calendarEvent = CalendarEventBuilder.Build(extracted);
        try
        {
            return (await calendar.UpdateAsync(eventId, calendarEvent, keepPreviousDetails)).Id;
        }
        catch (Exception ex) when (ex is KeyNotFoundException || JujuCalendar.IsGone(ex))
        {
            return (await calendar.InsertAsync(calendarEvent)).Id;
        }
    }

    static Task RecordSheetItemAsync(Proposal proposal, string eventId, string status) =>
        SyncStore.SetAsync(proposal.Gid, proposal.Rk, proposal.Key, proposal.Hash, eventId, status, proposal.Event.Title);

    async Task ReleaseClaimAsync(string proposalId)
    {
        try { await PendingStore.ReleaseAsync(proposalId); }
        catch (RequestFailedException ex) { log.LogWarning(ex, "Could not release proposal {ProposalId} after a failed action", proposalId); }
    }
}
