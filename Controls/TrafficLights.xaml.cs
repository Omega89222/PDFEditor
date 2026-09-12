using System.Windows;
using System.Windows.Controls;

namespace PDFEditor.Controls;

/// <summary>
/// Les trois « feux tricolores » de la barre de titre macOS :
/// fermer, reduire, agrandir. Les glyphes n'apparaissent qu'au survol
/// du groupe et les pastilles grisent quand la fenetre perd le focus.
/// </summary>
public partial class TrafficLights : UserControl
{
    public TrafficLights()
    {
        InitializeComponent();
    }

    /// <summary>Fenetre hote (null en mode createur).</summary>
    private Window? HostWindow => Window.GetWindow(this);

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        HostWindow?.Close();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        var window = HostWindow;
        if (window is null)
        {
            return;
        }

        window.WindowState = WindowState.Minimized;
    }

    private void OnZoomClick(object sender, RoutedEventArgs e)
    {
        var window = HostWindow;
        if (window is null)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
