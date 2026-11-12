using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SchoolAssistant;

/// <summary>What one cleanup removed.</summary>
public sealed record CleanupResult(int Messages, int AnsweredQuestions, int SheetRecords, DateOnly KeptFrom)
{
    public int Total => Messages + AnsweredQuestions + SheetRecords;

    /// <summary>The Telegram reply.</summary>
    public string Describe() => Total == 0
        ? $"🧹 Nothing to clean up before {DateText.Day(KeptFrom)}."
        : $"🧹 Cleaned up everything before {DateText.Day(KeptFrom)}: {Messages} messages, {AnsweredQuestions} answered questions, {SheetRecords} old sheet records.";
}

/// <summary>
/// Removes stored records from before the current month. Nothing here is needed again: repeated Telegram updates only
/// arrive within hours, answered questions are done, and past sheet items are never back-filled. Unanswered questions
/// and the per-tab sheet fingerprints are always kept.
/// </summary>
public class StorageCleanup(ILogger<StorageCleanup> log)
{
    public async Task<CleanupResult> RunAsync()
    {
        var today = ManilaTime.Today;
        var firstDayToKeep = new DateOnly(today.Year, today.Month, 1);
        var cutoff = CalendarEventBuilder.At(firstDayToKeep, TimeOnly.MinValue);

        var result = new CleanupResult(
            await MessageStore.DeleteOlderThanAsync(cutoff),
            await PendingStore.DeleteAnsweredOlderThanAsync(cutoff),
            await SyncStore.DeleteStartingBeforeAsync(firstDayToKeep),
            firstDayToKeep);

        log.LogInformation("Cleanup before {FirstDayToKeep}: {Messages} messages, {Questions} questions, {Records} sheet records",
            firstDayToKeep, result.Messages, result.AnsweredQuestions, result.SheetRecords);
        return result;
    }
}

/// <summary>Runs the cleanup on the first day of every month, and only speaks up if something was removed.</summary>
public class CleanupTimer(StorageCleanup cleanup, TelegramApi telegram)
{
    const string MonthlyAt3Am = "0 0 3 1 * *"; // NCRONTAB (second minute hour day month weekday), UTC

    [Function("storage-cleanup")]
    public async Task Run([TimerTrigger(MonthlyAt3Am)] TimerInfo timer)
    {
        var result = await cleanup.RunAsync();
        if (result.Total > 0) await telegram.SendAsync(Env.TelegramChatId, result.Describe());
    }
}
