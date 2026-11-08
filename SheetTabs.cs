using System.Net;
using Google;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

namespace SchoolAssistant;

/// <summary>Finds the tabs of the tracker spreadsheet, so new months are picked up without changing settings.</summary>
public interface ISheetTabLister
{
    /// <summary>The gid of every visible tab, in sheet order.</summary>
    Task<IReadOnlyList<string>> ListGidsAsync(string sheetId);
}

/// <summary>Lists tabs through the Google Sheets API, signed in as the same service account as the calendar.</summary>
public class GoogleSheetTabLister : ISheetTabLister
{
    const string GoogleAppName = "SchoolNotifs";
    const string TabFields = "sheets.properties(sheetId,hidden)";

    readonly Lazy<SheetsService> _service;

    public GoogleSheetTabLister() => _service = new(CreateService);

    /// <summary>For tests: use an already-built service (for example one backed by a fake HTTP handler).</summary>
    internal GoogleSheetTabLister(SheetsService service) => _service = new(service);

    internal static SheetsService CreateService() => new(new BaseClientService.Initializer
    {
        HttpClientInitializer = GoogleCredentials.ForServiceAccount(SheetsService.Scope.SpreadsheetsReadonly),
        ApplicationName = GoogleAppName
    });

    public async Task<IReadOnlyList<string>> ListGidsAsync(string sheetId)
    {
        var request = _service.Value.Spreadsheets.Get(sheetId);
        request.Fields = TabFields;
        try
        {
            var spreadsheet = await request.ExecuteAsync();
            return (spreadsheet.Sheets ?? new List<Google.Apis.Sheets.v4.Data.Sheet>())
                .Where(tab => tab.Properties?.SheetId != null && tab.Properties.Hidden != true)
                .Select(tab => tab.Properties.SheetId!.Value.ToString())
                .ToList();
        }
        catch (GoogleApiException ex) when (Explain(ex, sheetId) is { } explanation)
        {
            throw new InvalidOperationException(explanation, ex);
        }
    }

    /// <summary>A plain-language cause for the errors people can fix; null for anything else.</summary>
    static string? Explain(GoogleApiException ex, string sheetId) => ex.HttpStatusCode switch
    {
        HttpStatusCode.Forbidden =>
            $"Can't list the tabs of sheet {sheetId} (403). Share the sheet with the service account as Viewer and enable the Google Sheets API, or set {Settings.SheetGids}.",
        HttpStatusCode.NotFound =>
            $"Sheet {sheetId} not found (404). Check {Settings.SheetId}.",
        HttpStatusCode.BadRequest =>
            $"Google rejected sheet {sheetId} (400). Check {Settings.SheetId}.",
        _ => null
    };
}
