using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PDFEditor.Models;

namespace PDFEditor.Services;

/// <summary>Cles des styles memorises par outil.</summary>
public static class ToolKeys
{
    public const string Pen = "pen";
    public const string Highlighter = "highlighter";
    public const string Shape = "shape";
    public const string Line = "line";
    public const string Text = "text";
    public const string Highlight = "highlight";
    public const string Underline = "underline";
    public const string Strike = "strike";
    public const string Squiggly = "squiggly";
    public const string Note = "note";
    public const string Stamp = "stamp";
    public const string Whiteout = "whiteout";
}

/// <summary>
/// Reglages de l'application : %APPDATA%\PDFEditor\settings.json.
/// Ecriture atomique et differee ; aucune exception ne remonte a l'appelant.
/// </summary>
public static class SettingsService
{
    private const int MaxRecentFiles = 12;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static DispatcherTimer? _debounce;

    static SettingsService()
    {
        // PDFEDITOR_DATA_DIR permet d'isoler les reglages (captures automatiques, tests).
        var overrideDirectory = Environment.GetEnvironmentVariable("PDFEDITOR_DATA_DIR");
        DataDirectory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFEditor");
    }

    public static string DataDirectory { get; }

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static event Action? RecentFilesChanged;

    public static event Action? SignaturesChanged;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Lecture impossible : {ex.Message}");
        }

        Current.RecentFiles ??= new();
        Current.ToolStyles ??= new();
        Current.Signatures ??= new();
        Current.RecentColors ??= new();
        if (string.IsNullOrWhiteSpace(Current.AuthorName))
        {
            Current.AuthorName = Environment.UserName;
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Ecriture impossible : {ex.Message}");
        }
    }

    /// <summary>Enregistrement differe de 600 ms (regroupe les changements rapides).</summary>
    public static void SaveSoon()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Save();
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(SaveSoon));
            return;
        }

        if (_debounce is null)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                Save();
            };
        }

        _debounce.Stop();
        _debounce.Start();
    }

    public static void Flush()
    {
        if (_debounce?.IsEnabled == true)
        {
            _debounce.Stop();
        }

        Save();
    }

    // ------------------------------------------------------------ recents

    public static void AddRecent(string path, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Current.RecentFiles.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Current.RecentFiles.Insert(0, new RecentFile { Path = path, OpenedAt = DateTime.Now, PageCount = pageCount });
        if (Current.RecentFiles.Count > MaxRecentFiles)
        {
            Current.RecentFiles.RemoveRange(MaxRecentFiles, Current.RecentFiles.Count - MaxRecentFiles);
        }

        SaveSoon();
        RecentFilesChanged?.Invoke();
    }

    public static void RemoveRecent(string path)
    {
        Current.RecentFiles.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        SaveSoon();
        RecentFilesChanged?.Invoke();
    }

    public static void ClearRecent()
    {
        Current.RecentFiles.Clear();
        SaveSoon();
        RecentFilesChanged?.Invoke();
    }

    // ------------------------------------------------------------ styles

    public static ToolStyle GetToolStyle(string key)
    {
        if (!Current.ToolStyles.TryGetValue(key, out var style) || style is null)
        {
            style = CreateDefaultStyle(key);
            Current.ToolStyles[key] = style;
        }

        return style;
    }

    public static void ResetToolStyles()
    {
        Current.ToolStyles.Clear();
        SaveSoon();
    }

    private static ToolStyle CreateDefaultStyle(string key) => key switch
    {
        ToolKeys.Pen => new ToolStyle { StrokeColor = "#FFFF3B30", StrokeWidth = 2 },
        ToolKeys.Highlighter => new ToolStyle { StrokeColor = "#FFFFD60A", StrokeWidth = 14, Opacity = 0.45 },
        ToolKeys.Shape => new ToolStyle { StrokeColor = "#FFFF3B30", StrokeWidth = 2, FillColor = "#00000000" },
        ToolKeys.Line => new ToolStyle { StrokeColor = "#FFFF3B30", StrokeWidth = 2 },
        ToolKeys.Text => new ToolStyle { StrokeColor = "#FF1C1C1E", StrokeWidth = 0, FillColor = "#00000000", TextColor = "#FF1C1C1E", FontFamily = "Arial", FontSize = 14 },
        ToolKeys.Highlight => new ToolStyle { StrokeColor = "#FFFFD60A", Opacity = 0.5 },
        ToolKeys.Underline => new ToolStyle { StrokeColor = "#FF007AFF", Opacity = 1 },
        ToolKeys.Strike => new ToolStyle { StrokeColor = "#FFFF3B30", Opacity = 1 },
        ToolKeys.Squiggly => new ToolStyle { StrokeColor = "#FF34C759", Opacity = 1 },
        ToolKeys.Note => new ToolStyle { StrokeColor = "#FFFFCC00" },
        ToolKeys.Stamp => new ToolStyle { StrokeColor = "#FFD63A2F", StrokeWidth = 2.5 },
        ToolKeys.Whiteout => new ToolStyle { FillColor = "#FFFFFFFF", StrokeWidth = 0 },
        _ => new ToolStyle()
    };

    // ------------------------------------------------------------ signatures

    public static void AddSignature(SavedSignature signature)
    {
        Current.Signatures.Insert(0, signature);
        SaveSoon();
        SignaturesChanged?.Invoke();
    }

    public static void RemoveSignature(string id)
    {
        Current.Signatures.RemoveAll(s => s.Id == id);
        SaveSoon();
        SignaturesChanged?.Invoke();
    }

    // ------------------------------------------------------------ couleurs

    public static void AddRecentColor(Color color)
    {
        var hex = ColorUtil.ToHex(color);
        Current.RecentColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        Current.RecentColors.Insert(0, hex);
        if (Current.RecentColors.Count > 8)
        {
            Current.RecentColors = Current.RecentColors.Take(8).ToList();
        }

        SaveSoon();
    }
}
