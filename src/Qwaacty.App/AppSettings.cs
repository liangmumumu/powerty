using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Qwaacty.Core;

namespace Qwaacty.App;

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
    public bool ProfessionalModeEnabled { get; set; }
    public string ProfessionalHotkey { get; set; } = "Ctrl+Alt+Shift+P";

    /// <summary>Not persisted. True when the stored DPAPI blob could not be decrypted
    /// (different user/machine or corrupted); the original blob is kept so a later save
    /// cannot destroy the only recoverable copy of the key.</summary>
    [JsonIgnore]
    public bool ApiKeyDecryptFailed { get; set; }

    /// <summary>Not persisted: the original "dpapi:…" ciphertext preserved while
    /// decryption fails.</summary>
    [JsonIgnore]
    public string? PreservedApiKeyBlob { get; set; }
}

internal static class SettingsStore
{
    private static readonly string SettingsPath = PortableMode.ResolveSettingsPath(
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qwaacty"));

    private static readonly string SettingsDirectory = Path.GetDirectoryName(SettingsPath) ?? ".";

    public static AppSettings Load()
    {
        TryImportLegacySettings();
        if (!File.Exists(SettingsPath))
        {
            var fresh = new AppSettings { SettingsVersion = CurrentSettingsVersion };
            TrySave(fresh);
            return fresh;
        }

        var json = File.ReadAllText(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        // System.Text.Json does not enforce non-nullable reference types: a corrupted or
        // hand-edited "AiApiKey": null used to throw an NRE here, crash the app while the
        // window was being constructed, and leave the single-instance mutex behind —
        // after which the app could never be started again.
        var storedKey = settings.AiApiKey ?? string.Empty;
        settings.AiApiKey = storedKey;
        // A pre-DPAPI build stored the key as plain text; rewrite right away so the
        // file stops containing a readable secret even before the next settings change.
        var plaintextKeyNeedsMigration =
            storedKey.Length > 0 && !storedKey.StartsWith(Dpapi.Prefix, StringComparison.Ordinal);
        var unprotectedKey = Dpapi.Unprotect(storedKey);
        if (unprotectedKey is null)
        {
            // Decryption failed (wrong user/machine or a corrupted blob): keep the
            // ciphertext so a later save cannot overwrite it with an empty string and
            // destroy the only recoverable copy of the key.
            settings.ApiKeyDecryptFailed = true;
            settings.PreservedApiKeyBlob = storedKey;
            settings.AiApiKey = string.Empty;
            DiagnosticsLog.Write("SETTINGS", $"ai key not decryptable (win32={Dpapi.LastError}); ciphertext preserved");
        }
        else
        {
            settings.AiApiKey = unprotectedKey;
        }

        var needsRewrite = plaintextKeyNeedsMigration;

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

        if (settings.SettingsVersion < 13)
        {
            // v12 introduced the input-translation module (later reverted); v13 adds the
            // professional (electronics) dictionary module. Both only need defaults.
            needsRewrite = true;
        }

        if (string.IsNullOrWhiteSpace(settings.ProfessionalHotkey))
        {
            settings.ProfessionalHotkey = "Ctrl+Alt+Shift+P";
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

        if (settings.SettingsVersion > CurrentSettingsVersion)
        {
            // A newer (or reverted) build wrote a higher version number; clamp it so
            // migrations keyed on this number still run for these users instead of being
            // silently skipped.
            DiagnosticsLog.Write("SETTINGS", $"settings version clamped {settings.SettingsVersion} -> {CurrentSettingsVersion}");
            settings.SettingsVersion = CurrentSettingsVersion;
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

    /// <summary>The app was previously named Powerty; import its settings once so the
    /// rename does not lose hotkeys or the AI key (DPAPI blobs are user-scoped and
    /// remain decryptable under the new name).</summary>
    private static void TryImportLegacySettings()
    {
        try
        {
            if (File.Exists(SettingsPath) || PortableMode.IsPortable(AppContext.BaseDirectory))
            {
                return;
            }

            var legacyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Powerty");
            var legacyPath = Path.Combine(legacyDirectory, PortableMode.SettingsFileName);
            if (!File.Exists(legacyPath))
            {
                return;
            }

            Directory.CreateDirectory(SettingsDirectory);
            File.Copy(legacyPath, SettingsPath);
            // Pre-DPAPI builds kept the AI key in plain text inside the legacy folder
            // (main file, .bak and .tmp). Purge them after the import so no readable
            // copy of the key survives on disk.
            PurgeLegacyFile(legacyPath);
            PurgeLegacyFile(legacyPath + ".bak");
            PurgeLegacyFile(legacyPath + ".tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    /// <summary>Deletes a legacy settings file; when deletion is blocked the content is
    /// at least blanked so a plain-text secret does not remain readable.</summary>
    private static void PurgeLegacyFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.WriteAllText(path, "{}");
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private const int CurrentSettingsVersion = 13;

    /// <summary>Set when the most recent Save could not encrypt the AI key; the plain
    /// text was then NOT written to disk and callers surface this to the user.</summary>
    public static bool LastSaveKeyProtectionFailed { get; private set; }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        LastSaveKeyProtectionFailed = false;
        var plainKey = settings.AiApiKey ?? string.Empty;
        string encryptedKey;
        if (plainKey.Length == 0)
        {
            // Preserve an undecryptable blob so a save triggered by an unrelated setting
            // change cannot silently destroy the only recoverable copy of the key.
            encryptedKey = settings.ApiKeyDecryptFailed && !string.IsNullOrEmpty(settings.PreservedApiKeyBlob)
                ? settings.PreservedApiKeyBlob!
                : string.Empty;
        }
        else
        {
            encryptedKey = Dpapi.Protect(plainKey) ?? string.Empty;
            if (encryptedKey.Length == 0)
            {
                // Encryption failed: never persist the key in plain text. Keep it for
                // this session only and report the failure to the caller.
                LastSaveKeyProtectionFailed = true;
                DiagnosticsLog.Write("SETTINGS", $"dpapi protect failed (win32={Dpapi.LastError}); ai key kept in memory only");
            }
            else
            {
                // A fresh, encryptable key replaces any preserved undecryptable blob.
                settings.ApiKeyDecryptFailed = false;
                settings.PreservedApiKeyBlob = null;
            }
        }

        settings.AiApiKey = encryptedKey;
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

        // File.Replace turns the previous settings file into .bak; on machines
        // upgrading from a pre-DPAPI build that file can still hold the key in plain
        // text. Overwrite .bak with the just-written (encrypted) content so no readable
        // secret survives on disk. Best effort: the main file is authoritative anyway.
        try
        {
            File.WriteAllText(SettingsPath + ".bak", json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApplicationName = "Qwaacty";
    private const string LegacyApplicationName = "Powerty";

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

    /// <summary>Moves a pre-rename "Powerty" autostart entry to the new name so login
    /// autostart keeps working and no longer points at the old folder.</summary>
    public static void MigrateLegacyValue()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey?.GetValue(LegacyApplicationName) is not string legacy || legacy.Length == 0)
            {
                return;
            }

            if (runKey.GetValue(ApplicationName) is null && Environment.ProcessPath is { } executablePath)
            {
                runKey.SetValue(ApplicationName, $"\"{executablePath}\"", RegistryValueKind.String);
            }

            runKey.DeleteValue(LegacyApplicationName, throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }
}
