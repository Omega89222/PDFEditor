using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows.Media;
using PDFEditor.Models;

namespace PDFEditor.ViewModels;

public enum AlertIcon { None, Info, Warning, Question, Error, Lock }

public enum FormFieldKind { Text, MultilineText, Password, Number, Choice, Check, Switch, Slider, Color, Label, Separator }

/// <summary>Champ d'un formulaire de dialogue (construit par la vue).</summary>
public sealed class FormField
{
    public required string Key { get; init; }
    public FormFieldKind Kind { get; init; }
    public string Label { get; init; } = "";
    public object? Value { get; set; }
    public string? Placeholder { get; init; }
    public string? Help { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 100;
    public double Step { get; init; } = 1;
    public int Decimals { get; init; }
    public string? Suffix { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
}

/// <summary>Description d'un dialogue de saisie, independante de WPF.</summary>
public sealed class FormSpec
{
    public FormSpec(string title)
    {
        Title = title;
    }

    public string Title { get; }
    public string? Message { get; init; }
    public string ConfirmLabel { get; init; } = "OK";
    public string CancelLabel { get; init; } = "Annuler";
    public double Width { get; init; } = 440;
    public AlertIcon Icon { get; init; }
    public List<FormField> Fields { get; } = new();

    /// <summary>Retourne un message d'erreur, ou null si la saisie est valide.</summary>
    public Func<FormResult, string?>? Validate { get; init; }

    public FormSpec Text(string key, string label, string value = "", string? placeholder = null, string? help = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Text, Label = label, Value = value, Placeholder = placeholder, Help = help });
        return this;
    }

    public FormSpec Multiline(string key, string label, string value = "", string? placeholder = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.MultilineText, Label = label, Value = value, Placeholder = placeholder });
        return this;
    }

    public FormSpec Password(string key, string label, string? placeholder = null, string? help = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Password, Label = label, Value = "", Placeholder = placeholder, Help = help });
        return this;
    }

    public FormSpec Number(string key, string label, double value, double min, double max, double step = 1, int decimals = 0, string? suffix = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Number, Label = label, Value = value, Minimum = min, Maximum = max, Step = step, Decimals = decimals, Suffix = suffix });
        return this;
    }

    public FormSpec Choice(string key, string label, IReadOnlyList<string> options, int selected = 0, string? help = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Choice, Label = label, Options = options, Value = selected, Help = help });
        return this;
    }

    public FormSpec Check(string key, string label, bool value = false, string? help = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Check, Label = label, Value = value, Help = help });
        return this;
    }

    public FormSpec Switch(string key, string label, bool value = false, string? help = null)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Switch, Label = label, Value = value, Help = help });
        return this;
    }

    public FormSpec Slider(string key, string label, double value, double min, double max, string? suffix = null, int decimals = 0)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Slider, Label = label, Value = value, Minimum = min, Maximum = max, Suffix = suffix, Decimals = decimals });
        return this;
    }

    public FormSpec ColorPick(string key, string label, Color value)
    {
        Fields.Add(new FormField { Key = key, Kind = FormFieldKind.Color, Label = label, Value = value });
        return this;
    }

    public FormSpec Info(string text)
    {
        Fields.Add(new FormField { Key = "info" + Fields.Count, Kind = FormFieldKind.Label, Label = text });
        return this;
    }

    public FormSpec Separator()
    {
        Fields.Add(new FormField { Key = "sep" + Fields.Count, Kind = FormFieldKind.Separator });
        return this;
    }
}

public sealed class FormResult
{
    public Dictionary<string, object?> Values { get; } = new();

    public string String(string key) => Values.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    public double Number(string key)
    {
        if (!Values.TryGetValue(key, out var v) || v is null)
        {
            return 0;
        }

        return v switch
        {
            double d => d,
            int i => i,
            string s when double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    public bool Bool(string key) => Values.TryGetValue(key, out var v) && v is true;

    public int Index(string key) => Values.TryGetValue(key, out var v) && v is int i ? i : 0;

    public Color Color(string key) => Values.TryGetValue(key, out var v) && v is Color c ? c : Colors.Black;
}

public interface IProgressReporter : IDisposable
{
    CancellationToken Token { get; }

    void Report(double fraction, string? message = null);
}

/// <summary>Services d'interface demandes par les modeles de vue.</summary>
public interface IDialogService
{
    string? OpenFile(string title, string filter);

    string[] OpenFiles(string title, string filter);

    string? SaveFile(string title, string filter, string suggestedName, string? directory = null);

    string? PickFolder(string title);

    /// <summary>Alerte facon macOS ; retourne l'index du bouton choisi.</summary>
    int Alert(string title, string message, IReadOnlyList<string> buttons, int defaultButton = 0, int cancelButton = -1, int destructiveButton = -1, AlertIcon icon = AlertIcon.Info);

    string? Prompt(string title, string message, string value = "", string? placeholder = null, bool password = false, string confirm = "OK");

    FormResult? ShowForm(FormSpec spec);

    SavedSignature? CreateSignature();

    IProgressReporter BeginProgress(string title, string message, bool cancellable);

    void OpenInNewWindow(DocumentViewModel document);

    void OpenPathInNewWindow(string path);

    void OpenNewWindow();

    void ShowShortcuts();

    void ShowAbout();

    void FocusSearch();

    void Print(DocumentViewModel document);
}

/// <summary>Reglages du filigrane.</summary>
public sealed record WatermarkOptions(string Text, string FontFamily, bool Bold, double FontSize, Color Color, double Opacity, double Angle);

public enum HeaderFooterPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Reglages de la numerotation / en-tete / pied de page.</summary>
public sealed record HeaderFooterOptions(string Format, HeaderFooterPosition Position, int StartNumber, string FontFamily, double FontSize, Color Color, double Margin, bool SkipFirstPage);
