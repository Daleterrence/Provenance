namespace Provenance.Common;

public static class HttpAgent
{
    public const string Provenance = "Provenance/1.0";

    public static void Set(HttpClient http) => http.DefaultRequestHeaders.UserAgent.ParseAdd(Provenance);
}
