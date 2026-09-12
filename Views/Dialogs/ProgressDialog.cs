using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Dialogs;

/// <summary>
/// Fenetre de progression pour les traitements longs (OCR, exports...).
/// La fenetre proprietaire est desactivee pendant le traitement ; le travail
/// asynchrone continue a s'executer normalement.
/// </summary>
public sealed class ProgressDialog : IProgressReporter
{
    private readonly MacDialog _window;
    private readonly ProgressBar _bar;
    private readonly TextBlock _message;
    private readonly Window? _owner;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _disposed;

    public ProgressDialog(Window? owner, string title, string message, bool cancellable)
    {
        _owner = owner;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _window = new MacDialog(owner, 360) { CloseOnEscape = false };

        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var icon = AppIcon.Create(40);
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(MacDialog.Text(title, "DialogTitle"));
        _message = MacDialog.Text(message, "CaptionText", new Thickness(0, 3, 0, 0));
        titles.Children.Add(_message);
        header.Children.Add(titles);
        root.Children.Add(header);

        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0 };
        _bar.SetResourceReference(FrameworkElement.StyleProperty, "MacProgressBar");
        root.Children.Add(_bar);

        if (cancellable)
        {
            var cancel = new Button { Content = "Annuler", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "MacButton");
            cancel.Click += (_, _) => RequestCancel(cancel);
            _window.EscapePressed += () => RequestCancel(cancel);
            root.Children.Add(cancel);
        }

        _window.Body = root;

        if (_owner is not null)
        {
            _owner.IsEnabled = false;
        }

        _window.Show();
    }

    public CancellationToken Token => _cancellation.Token;

    private void RequestCancel(Button button)
    {
        _cancellation.Cancel();
        button.IsEnabled = false;
        _message.Text = "Annulation…";
    }

    public void Report(double fraction, string? message = null)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
            {
                return;
            }

            _bar.Value = Math.Clamp(fraction, 0, 1);
            if (message is not null && !_cancellation.IsCancellationRequested)
            {
                _message.Text = message;
            }
        }));
    }

    public void Dispose()
    {
        void Close()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_owner is not null)
            {
                _owner.IsEnabled = true;
                _owner.Activate();
            }

            _window.Close();
        }

        if (_dispatcher.CheckAccess())
        {
            Close();
        }
        else
        {
            _dispatcher.Invoke(Close);
        }
    }
}
