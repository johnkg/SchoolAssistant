using System.Net;
using System.Security.Cryptography;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Xunit;

namespace SchoolAssistant.Tests;

public class GoogleSheetTabListerTests : AppTestBase
{
    static GoogleSheetTabLister ListerAnswering(string json, HttpStatusCode status, out StubHandler handler)
    {
        handler = new StubHandler(_ => StubHandler.Json(json, status));
        var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeGoogleHttpFactory(handler),
            ApplicationName = "tests",
            GZipEnabled = false
        });
        return new GoogleSheetTabLister(service);
    }

    static string Error(HttpStatusCode status) => $"{{\"error\":{{\"code\":{(int)status},\"message\":\"{status}\"}}}}";

    [Fact]
    public async Task Lists_the_gid_of_each_visible_tab_in_order()
    {
        var lister = ListerAnswering("""
            {"sheets":[{"properties":{"sheetId":721468625}},{"properties":{"sheetId":1460021000,"hidden":false}},
            {"properties":{"sheetId":5,"hidden":true}},{"properties":{"sheetId":0}},{"properties":{}},{}]}
            """, HttpStatusCode.OK, out var handler);

        Assert.Equal(new[] { "721468625", "1460021000", "0" }, await lister.ListGidsAsync("sheet-1"));

        var request = handler.Requests.Single();
        Assert.Contains("/spreadsheets/sheet-1", request.Uri.AbsolutePath);
        Assert.Contains("fields=sheets.properties(sheetId,hidden)", Uri.UnescapeDataString(request.Uri.Query));
    }

    [Fact] public async Task A_sheet_with_no_tabs_gives_an_empty_list() => Assert.Empty(await ListerAnswering("{}", HttpStatusCode.OK, out _).ListGidsAsync("s"));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Can't list the tabs of sheet s (403). Share the sheet with the service account as Viewer and enable the Google Sheets API, or set SHEET_GIDS.")]
    [InlineData(HttpStatusCode.NotFound, "Sheet s not found (404). Check SHEET_ID.")]
    [InlineData(HttpStatusCode.BadRequest, "Google rejected sheet s (400). Check SHEET_ID.")]
    public async Task Fixable_errors_are_explained(HttpStatusCode status, string explanation)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ListerAnswering(Error(status), status, out _).ListGidsAsync("s"));
        Assert.Equal(explanation, ex.Message);
        Assert.IsType<Google.GoogleApiException>(ex.InnerException);
    }

    [Fact]
    public async Task Other_errors_are_passed_through_unchanged() =>
        await Assert.ThrowsAsync<Google.GoogleApiException>(() => ListerAnswering(Error(HttpStatusCode.Conflict), HttpStatusCode.Conflict, out _).ListGidsAsync("s"));

    [Fact]
    public void The_public_constructor_is_lazy_and_needs_no_settings() => Assert.NotNull(new GoogleSheetTabLister());

    [Fact]
    public void The_service_signs_in_as_the_service_account_with_read_only_scope()
    {
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");
        SetEnv(Settings.GoogleServiceAccountJson,
            $$"""{"type":"service_account","project_id":"p","client_email":"bot@p.iam.gserviceaccount.com","private_key":"{{key}}","token_uri":"https://oauth2.googleapis.com/token"}""");

        Assert.NotNull(GoogleSheetTabLister.CreateService());
    }
}
