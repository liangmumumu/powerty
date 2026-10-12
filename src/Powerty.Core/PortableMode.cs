namespace Powerty.Core;

public static class PortableMode
{
    public const string MarkerFileName = "portable.txt";
    public const string SettingsFileName = "settings.json";

    public static bool IsPortable(string baseDirectory) =>
        File.Exists(Path.Combine(baseDirectory, MarkerFileName));

    public static string ResolveSettingsPath(string baseDirectory, string fallbackDirectory)
    {
        var directory = IsPortable(baseDirectory) ? baseDirectory : fallbackDirectory;
        return Path.Combine(directory, SettingsFileName);
    }
}
