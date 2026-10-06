using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenUsage.Core.Support;

namespace OpenUsage.Windows;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// Loads the light or dark palette (Themes/*.xaml) into the app resources. Follows Windows' app mode, live,
/// unless the user picks Light or Dark from the panel; that choice is saved to %LOCALAPPDATA%\OpenUsage\settings.json.
public sealed class ThemeManager : Observable, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUsage", "settings.json");

    private static readonly string AssemblyName = typeof(ThemeManager).Assembly.GetName().Name!;

    private readonly ResourceDictionary _resources;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private ThemeChoice _choice;
    private bool? _appliedLight;

    /// `resources` holds the palette as its first merged dictionary (see App.xaml).
    public ThemeManager(ResourceDictionary resources)
    {
        _resources = resources;
        _choice = Load();
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public ThemeChoice Choice { get => _choice; private set => Set(ref _choice, value); }

    /// System → Light → Dark → System.
    public void Cycle()
    {
        Choice = Choice switch
        {
            ThemeChoice.System => ThemeChoice.Light,
            ThemeChoice.Light => ThemeChoice.Dark,
            _ => ThemeChoice.System,
        };
        Save();
        Apply();
    }

    private void Apply()
    {
        var light = Choice switch
        {
            ThemeChoice.Light => true,
            ThemeChoice.Dark => false,
            _ => AppsUseLightTheme(),
        };
        if (light == _appliedLight) return;
        _appliedLight = light;
        _resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{AssemblyName};component/Themes/{(light ? "Light" : "Dark")}.xaml"),
        };
    }

    /// Settings → Personalization → Colors → "Choose your default app mode". Builds without dark mode lack the value.
    private static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    /// Changing the app mode raises a General preference change, on a SystemEvents thread.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) _dispatcher.InvokeAsync(Apply);
    }

    private static ThemeChoice Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThemeChoice.System;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            return Enum.TryParse<ThemeChoice>(settings?.Theme, out var choice) && Enum.IsDefined(choice) ? choice : ThemeChoice.System;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Could not read settings", e);
            return ThemeChoice.System;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(Choice.ToString())));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings", e);
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private sealed record Settings(string? Theme);
}
