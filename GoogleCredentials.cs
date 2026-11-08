using System.Text;
using Google.Apis.Auth.OAuth2;

namespace SchoolAssistant;

/// <summary>The Google service account that the calendar and sheet clients sign in as.</summary>
internal static class GoogleCredentials
{
    /// <summary>
    /// GOOGLE_SA_JSON is the service account key file: raw JSON, or the same thing base64-encoded.
    /// </summary>
    public static GoogleCredential ForServiceAccount(string scope)
    {
        var raw = Env.Get(Settings.GoogleServiceAccountJson).Trim();
        var json = raw.StartsWith('{') ? raw : Encoding.UTF8.GetString(Convert.FromBase64String(raw));
#pragma warning disable CS0618
        return GoogleCredential.FromJson(json).CreateScoped(scope);
#pragma warning restore CS0618
    }
}
