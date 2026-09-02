// No running Host or user data is needed for UpdateService contract tests.
namespace TraceSoul2.Host
{
    public sealed class TraceHomeLayout
    {
        public string Root;
        public string UpdatesDirectory;
        public string PluginsDirectory;
        public string UpdateRepository;
        public string Urls;
    }
    internal static class TraceHome
    {
        public const string EnvHome = "TRACESOUL2_HOME";
        public const string EnvPlugins = "TRACESOUL2_PLUGINS";
        public const string EnvUrls = "TRACESOUL2_URLS";
        public static string HostVersion() => "0.0.1";
        public static void RememberUpdateRepository(string repository) { }
    }
}
