using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Powerty.Core;

namespace Powerty.App;

internal sealed class AppSettings
{
    public int SettingsVersion { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Alt+Shift+T";
    public bool AllowOnlineLookup { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string PopupFontFamily { get; set; } = "Segoe UI";
    public double PopupFontSize { get; set; } = 16;
    public double PopupOpacity { get; set; } = 0.95;
    public double PopupWidth { get; set; } = 760;
    public double PopupMaxHeight { get; set; } = 84;
    public string PopupBackgroundColor { get; set; } = "#F21E1E1E";
    public string PopupTextColor { get; set; } = "#FFD4D4D4";
    public bool CompatibilityMode { get; set; } = true;
    public bool TypingLookupEnabled { get; set; } = true;
    public bool SelectionLookupEnabled { get; set; } = true;
    public bool SelectionHotkeyEnabled { get; set; } = true;
    public bool SelectionLookupAuto { get; set; }
    public bool SelectionHotkeyAutoClose { get; set; } = true;
    public bool DiagnosticsEnabled { get; set; } = true;
    public bool AiEnabled { get; set; }
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com";
    public string AiModel { get; set; } = "deepseek-chat";
    public string AiApiKey { get; set; } = "";
    public bool AiStreaming { get; set; } = true;
    public bool AiContextEnhance { get; set; }
    public string SelectionHotkey { get; set; } = "Ctrl+Alt+Shift+D";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+Shift+X";
    public string SelectionToggleHotkey { get; set; } = "Ctrl+Alt+Shift+S";
    public string SelectionAutoToggleHotkey { get; set; } = "Ctrl+Alt+Shift+A";
}

internal static class SettingsStore
{
    private static readonly string SettingsPath = PortableMode.ResolveSettingsPath(
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Powerty"));

    private static readonly string SettingsDirectory = Path.GetDirectoryName(SettingsPath) ?? ".";

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var fresh = new AppSettings { SettingsVersion = CurrentSettingsVersion };
            TrySave(fresh);
            return fresh;
        }

        var json = File.ReadAllText(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        settings.AiApiKey = Dpapi.Unprotect(settings.AiApiKey);
        var needsRewrite = false;

        // v1 stored the selection auto-trigger default as true; the feature now defaults to hotkey-only.
        if (settings.SettingsVersion < 2)
        {
            settings.SelectionLookupAuto = false;
            needsRewrite = true;
        }

        // v2 and older had no module switches; users upgrading wanted both modules working
        // (the old runtime-only master switch is replaced by persisted per-module switches).
        if (settings.SettingsVersion < 3)
        {
            settings.TypingLookupEnabled = true;
            settings.SelectionLookupEnabled = true;
            settings.CompatibilityMode = true;
            needsRewrite = true;
        }

        // v4 and older used common chord defaults that clash with other software;
        // replace them with out-of-the-way combos unless the user customized them.
        if (settings.SettingsVersion < 5)
        {
            if (settings.Hotkey is "Ctrl+Shift+Space")
            {
                settings.Hotkey = "Ctrl+Alt+Shift+T";
            }

            if (settings.SelectionHotkey is "Ctrl+Shift+D")
            {
                settings.SelectionHotkey = "Ctrl+Alt+Shift+D";
            }

            if (settings.SelectionToggleHotkey is "Ctrl+Shift+Q")
            {
                settings.SelectionToggleHotkey = "Ctrl+Alt+Shift+S";
            }

            needsRewrite = true;
        }

        if (settings.SettingsVersion < 9)
        {
            settings.SelectionHotkeyEnabled = settings.SelectionLookupEnabled;
            settings.SelectionLookupEnabled = true;
            needsRewrite = true;
        }

        if (settings.SettingsVersion < 10)
        {
            settings.PopupWidth = 380;
            settings.PopupMaxHeight = 300;
            needsRewrite = true;
        }

        if (settings.SettingsVersion < 11)
        {
            settings.PopupWidth = 760;
            settings.PopupMaxHeight = 84;
            needsRewrite = true;
        }

        if (string.IsNullOrWhiteSpace(settings.PopupBackgroundColor))
        {
            settings.PopupBackgroundColor = "#F21E1E1E";
            needsRewrite = true;
        }

        if (string.IsNullOrWhiteSpace(settings.PopupTextColor))
        {
            settings.PopupTextColor = "#FFD4D4D4";
            needsRewrite = true;
        }

        if (settings.SettingsVersion < CurrentSettingsVersion)
        {
            settings.SettingsVersion = CurrentSettingsVersion;
            needsRewrite = true;
        }

        if (settings.PopupWidth < 280)
        {
            settings.PopupWidth = 380;
            needsRewrite = true;
        }
        else if (settings.PopupWidth > 1200)
        {
            settings.PopupWidth = 1200;
            needsRewrite = true;
        }

        if (settings.PopupMaxHeight < 40)
        {
            settings.PopupMaxHeight = 84;
            needsRewrite = true;
        }
        else if (settings.PopupMaxHeight > 1000)
        {
            settings.PopupMaxHeight = 1000;
            needsRewrite = true;
        }

        if (string.IsNullOrWhiteSpace(settings.SelectionHotkey))
        {
            settings.SelectionHotkey = "Ctrl+Alt+Shift+D";
            needsRewrite = true;
        }

        if (string.IsNullOrWhiteSpace(settings.SelectionToggleHotkey))
        {
            settings.SelectionToggleHotkey = "Ctrl+Alt+Shift+S";
            needsRewrite = true;
        }

        if (string.IsNullOrWhiteSpace(settings.ScreenshotHotkey))
        {
            settings.ScreenshotHotkey = "Ctrl+Alt+Shift+X";
            needsRewrite = true;
        }

        if (needsRewrite)
        {
            TrySave(settings);
        }

        return settings;
    }

    private static void TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const int CurrentSettingsVersion = 11;

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        // Encrypt the API key before it touches disk (DPAPI, current-user scope).
        var plainKey = settings.AiApiKey;
        settings.AiApiKey = Dpapi.Protect(plainKey) ?? plainKey;
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            settings.AiApiKey = plainKey;
        }

        // Atomic-ish write: never truncate the live file; keep one .bak so a torn
        // write cannot silently reset every setting (including the AI key).
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        if (File.Exists(SettingsPath))
        {
            File.Replace(temporaryPath, SettingsPath, SettingsPath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, SettingsPath);
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApplicationName = "Powerty";

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = runKey?.GetValue(ApplicationName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动配置。");

        if (!enabled)
        {
            runKey.DeleteValue(ApplicationName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定应用程序路径。");
        runKey.SetValue(ApplicationName, $"\"{executablePath}\"", RegistryValueKind.String);
    }
}
