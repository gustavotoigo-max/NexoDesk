using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace NexoDesk.Ui;

public enum ThemeMode
{
    /// <summary>Segue o tema de aplicativos do Windows.</summary>
    Auto,
    Light,
    Dark,
}

/// <summary>
/// Instala o Nexotool UI no aplicativo e troca entre claro e escuro em tempo de execução.
/// A escolha é salva em %AppData%\Nexotool\ui.json e vale para todos os aplicativos Nexotool.
/// </summary>
public static class ThemeManager
{
    private const string Base = "pack://application:,,,/NexoDesk.Ui;component/Themes/";
    private static ResourceDictionary? _themeSlot;
    private static bool _listening;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;

    /// <summary>Tema efetivo depois de resolver "Automático".</summary>
    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexotool", "ui.json");

    /// <summary>Chame uma vez em App.OnStartup, antes de abrir janelas.</summary>
    public static void Install(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var dictionaries = app.Resources.MergedDictionaries;
        dictionaries.Add(new ResourceDictionary { Source = new Uri(Base + "Brand.xaml", UriKind.Absolute) });
        _themeSlot = new ResourceDictionary { Source = new Uri(Base + "Light.xaml", UriKind.Absolute) };
        dictionaries.Add(_themeSlot);
        dictionaries.Add(new ResourceDictionary { Source = new Uri(Base + "Controls.xaml", UriKind.Absolute) });

        Apply(LoadSavedMode(), save: false);

        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.Auto)
                    app.Dispatcher.InvokeAsync(() => Apply(ThemeMode.Auto, save: false));
            };
        }
    }

    public static void Apply(ThemeMode mode, bool save = true)
    {
        Mode = mode;
        bool dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => WindowsPrefersDark(),
        };

        if (_themeSlot is not null && Application.Current is { } app)
        {
            var dictionaries = app.Resources.MergedDictionaries;
            int index = dictionaries.IndexOf(_themeSlot);
            var next = new ResourceDictionary { Source = new Uri(Base + (dark ? "Dark.xaml" : "Light.xaml"), UriKind.Absolute) };
            if (index >= 0)
                dictionaries[index] = next;
            else
                dictionaries.Add(next);
            _themeSlot = next;
        }

        IsDark = dark;
        if (save)
            SaveMode(mode);
        ThemeChanged?.Invoke();
    }

    /// <summary>Lê "AppsUseLightTheme" do registro (0 = escuro).</summary>
    public static bool WindowsPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static ThemeMode LoadSavedMode()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return ThemeMode.Auto;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("theme", out JsonElement theme)
                && Enum.TryParse(theme.GetString(), ignoreCase: true, out ThemeMode mode))
                return mode;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return ThemeMode.Auto;
    }

    private static void SaveMode(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, $"{{\n  \"theme\": \"{mode}\"\n}}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferência visual: falhar ao salvar não deve interromper o aplicativo.
        }
    }
}
