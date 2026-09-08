using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

internal static class GlobalTransportProtectedYouTubeBootstrapper
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoaded),
            handledEventsToo: true);
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is MainWindow window)
        {
            window.AttachGlobalTransportAndProtectedYouTube();
        }
    }
}

public partial class MainWindow
{
    private bool _globalTransportAndProtectedYouTubeAttached;
    private Border? _globalTransportBar;
    private Slider? _globalTransportSlider;
    private Slider? _youtubeVolumeSlider;
    private TextBlock? _youtubeVolumeLabel;
    private double _youtubeVolume = 1d;

    internal void AttachGlobalTransportAndProtectedYouTube()
    {
        if (_globalTransportAndProtectedYouTubeAttached)
        {
            return;
        }

        _globalTransportAndProtectedYouTubeAttached = true;
        InstallGlobalTransportBar();
        ProtectYouTubeSurface();

        YouTubeWebView.CoreWebView2InitializationCompleted +=
            OnProtectedYouTubeInitializationCompleted;
        YouTubeWebView.NavigationCompleted += OnProtectedYouTubeNavigationCompleted;
        Closed += OnGlobalTransportAndProtectedYouTubeClosed;

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            ConfigureProtectedYouTubeCore(core);
            _ = InstallYouTubePhysicalInputBlockAsync(core);
            _ = ApplyYouTubeVolumeAsync(core);
        }
    }

    private void InstallGlobalTransportBar()
    {
        if (_globalTransportBar is not null || Content is not Grid root)
        {
            return;
        }

        var mainArea = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                Grid.GetColumn(grid) == 1 &&
                grid.RowDefinitions.Count >= 3);
        if (mainArea is null)
        {
            return;
        }

        // La barra queda fuera de las páginas concretas, por eso sigue visible al cambiar entre
        // Practicar, Librerías, Pistas, YouTube y Dispositivos.
        mainArea.RowDefinitions.Insert(
            1,
            new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in mainArea.Children)
        {
            var row = Grid.GetRow(child);
            if (row >= 1)
            {
                Grid.SetRow(child, row + 1);
            }
        }

        var bar = BuildGlobalTransportBar();
        Grid.SetRow(bar, 1);

        // Se inserta pronto en el árbol visual para que la lógica común de transporte de YouTube
        // encuentre primero este slider global, en lugar del antiguo slider de la página Practicar.
        mainArea.Children.Insert(Math.Min(1, mainArea.Children.Count), bar);
        _globalTransportBar = bar;
    }

    private Border BuildGlobalTransportBar()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 22, 30)),
            BorderBrush = TryFindResource("Stroke") as Brush ??
                          new SolidColorBrush(Color.FromRgb(49, 56, 68)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 9, 18, 8)
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(new TextBlock
        {
            Text = "REPRODUCCIÓN GLOBAL",
            Foreground = TryFindResource("Accent") as Brush ?? Brushes.Goldenrod,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var title = new TextBlock
        {
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 470
        };
        title.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.CurrentTrackTitle))
            {
                Source = _viewModel,
                FallbackValue = "Control de reproducción"
            });
        identity.Children.Add(title);
        Grid.SetColumn(identity, 0);
        layout.Children.Add(identity);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        controls.Children.Add(CreateGlobalTransportButton(
            "Anterior",
            _viewModel.PreviousTrackCommand));
        controls.Children.Add(CreateGlobalTransportButton(
            "−10 s",
            _viewModel.SeekTrackCommand,
            "-10"));

        var play = CreateGlobalTransportButton(
            string.Empty,
            _viewModel.ToggleTrackCommand,
            primary: true);
        play.MinWidth = 104;
        play.SetBinding(
            ContentControl.ContentProperty,
            new Binding(nameof(MainViewModel.PlayButtonLabel)) { Source = _viewModel });
        controls.Children.Add(play);

        controls.Children.Add(CreateGlobalTransportButton(
            "Parar",
            _viewModel.StopTrackCommand));
        controls.Children.Add(CreateGlobalTransportButton(
            "+10 s",
            _viewModel.SeekTrackCommand,
            "10"));
        controls.Children.Add(CreateGlobalTransportButton(
            "Siguiente",
            _viewModel.NextTrackCommand));
        controls.Children.Add(BuildYouTubeVolumeControl());
        Grid.SetColumn(controls, 1);
        layout.Children.Add(controls);

        var sliderLine = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        sliderLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var position = new TextBlock
        {
            MinWidth = 45,
            Foreground = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        position.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.TrackPositionLabel)) { Source = _viewModel });
        sliderLine.Children.Add(position);

        var slider = new Slider
        {
            Height = 16,
            Margin = new Thickness(10, 0, 10, 0),
            Minimum = 0,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Posición de la reproducción activa"
        };
        AutomationProperties.SetName(slider, "Posición de reproducción");
        slider.SetBinding(
            Slider.MaximumProperty,
            new Binding(nameof(MainViewModel.TrackDurationSeconds))
            {
                Source = _viewModel,
                Mode = BindingMode.OneWay
            });
        slider.SetBinding(
            Slider.ValueProperty,
            new Binding(nameof(MainViewModel.TrackProgress))
            {
                Source = _viewModel,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        Grid.SetColumn(slider, 1);
        sliderLine.Children.Add(slider);
        _globalTransportSlider = slider;

        var duration = new TextBlock
        {
            MinWidth = 45,
            TextAlignment = TextAlignment.Right,
            Foreground = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        duration.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.TrackDurationLabel)) { Source = _viewModel });
        Grid.SetColumn(duration, 2);
        sliderLine.Children.Add(duration);

        Grid.SetRow(sliderLine, 1);
        Grid.SetColumnSpan(sliderLine, 2);
        layout.Children.Add(sliderLine);

        border.Child = layout;
        return border;
    }

    private FrameworkElement BuildYouTubeVolumeControl()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };

        panel.Children.Add(new TextBlock
        {
            Text = "YT",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Volumen de YouTube"
        });

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = _youtubeVolume * 100d,
            Width = 82,
            Height = 16,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Volumen de YouTube"
        };
        AutomationProperties.SetName(slider, "Volumen de YouTube");
        slider.ValueChanged += OnYouTubeVolumeChanged;
        panel.Children.Add(slider);
        _youtubeVolumeSlider = slider;

        var label = new TextBlock
        {
            Text = $"{Math.Round(_youtubeVolume * 100d):0}%",
            MinWidth = 34,
            Margin = new Thickness(5, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
            FontSize = 10,
            Foreground = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(label);
        _youtubeVolumeLabel = label;

        return panel;
    }

    private void OnYouTubeVolumeChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        _youtubeVolume = Math.Clamp(eventArgs.NewValue / 100d, 0d, 1d);
        if (_youtubeVolumeLabel is not null)
        {
            _youtubeVolumeLabel.Text = $"{Math.Round(_youtubeVolume * 100d):0}%";
        }

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            _ = ApplyYouTubeVolumeAsync(core);
        }
    }

    private async Task ApplyYouTubeVolumeAsync(CoreWebView2 core)
    {
        var volumeLiteral = _youtubeVolume.ToString("0.000", CultureInfo.InvariantCulture);
        var script = $$"""
            (() => {
              const desiredVolume = {{volumeLiteral}};
              window.__dpsDesiredYouTubeVolume = desiredVolume;

              const apply = () => {
                const volume = Number(window.__dpsDesiredYouTubeVolume);
                if (!Number.isFinite(volume)) return;
                document.querySelectorAll('video').forEach(video => {
                  try {
                    video.volume = Math.max(0, Math.min(1, volume));
                  } catch { }
                });
              };

              apply();
              if (!window.__dpsYouTubeVolumeObserver && document.documentElement) {
                window.__dpsYouTubeVolumeObserver = new MutationObserver(apply);
                window.__dpsYouTubeVolumeObserver.observe(
                  document.documentElement,
                  { childList: true, subtree: true });
              }
            })();
            """;

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ObjectDisposedException or
            System.Runtime.InteropServices.COMException)
        {
            // Una navegación puede reemplazar el documento mientras movemos el volumen.
        }
    }

    private Button CreateGlobalTransportButton(
        string text,
        System.Windows.Input.ICommand command,
        object? parameter = null,
        bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Command = command,
            CommandParameter = parameter,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (TryFindResource(primary ? "PrimaryButton" : "SecondaryButton") is Style style)
        {
            button.Style = style;
        }
        return button;
    }

    private void ProtectYouTubeSurface()
    {
        // La página sigue viva y los scripts de Drumless pueden manejarla, pero el usuario no puede
        // activar accidentalmente los controles nativos del reproductor con ratón o teclado. El
        // volumen que faltaba se controla ahora desde la barra global de Drumless.
        YouTubeWebView.Focusable = false;
        YouTubeWebView.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(YouTubeWebView, KeyboardNavigationMode.None);
    }

    private void OnProtectedYouTubeInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess || YouTubeWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        ConfigureProtectedYouTubeCore(core);
        _ = InstallYouTubePhysicalInputBlockAsync(core);
        _ = ApplyYouTubeVolumeAsync(core);
    }

    private async void OnProtectedYouTubeNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess && YouTubeWebView.CoreWebView2 is { } core)
        {
            await InstallYouTubePhysicalInputBlockAsync(core);
            await ApplyYouTubeVolumeAsync(core);
        }
    }

    private static void ConfigureProtectedYouTubeCore(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
    }

    private static async Task InstallYouTubePhysicalInputBlockAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync(YouTubePhysicalInputBlockScript);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ObjectDisposedException or
            System.Runtime.InteropServices.COMException)
        {
            // La navegación puede sustituir el documento mientras se instala la protección.
        }
    }

    private void OnGlobalTransportAndProtectedYouTubeClosed(object? sender, EventArgs eventArgs)
    {
        YouTubeWebView.CoreWebView2InitializationCompleted -=
            OnProtectedYouTubeInitializationCompleted;
        YouTubeWebView.NavigationCompleted -= OnProtectedYouTubeNavigationCompleted;
        Closed -= OnGlobalTransportAndProtectedYouTubeClosed;
        if (_youtubeVolumeSlider is not null)
        {
            _youtubeVolumeSlider.ValueChanged -= OnYouTubeVolumeChanged;
        }
        _youtubeVolumeLabel = null;
        _youtubeVolumeSlider = null;
        _globalTransportSlider = null;
        _globalTransportBar = null;
    }

    private const string YouTubePhysicalInputBlockScript =
        """
        (() => {
          if (window.__dpsPhysicalInputBlocked) return;
          window.__dpsPhysicalInputBlocked = true;

          const stopPhysicalInput = event => {
            if (!event.isTrusted) return;
            event.preventDefault();
            event.stopImmediatePropagation();
          };

          for (const type of [
            'click', 'dblclick', 'auxclick',
            'mousedown', 'mouseup', 'pointerdown', 'pointerup',
            'touchstart', 'touchmove', 'touchend',
            'wheel', 'contextmenu',
            'keydown', 'keyup', 'keypress',
            'dragstart', 'drop'
          ]) {
            window.addEventListener(type, stopPhysicalInput, {
              capture: true,
              passive: false
            });
          }

          const protectDocument = () => {
            if (!document.documentElement) return;
            document.documentElement.style.userSelect = 'none';
            document.documentElement.style.webkitUserSelect = 'none';
            document.documentElement.style.cursor = 'default';
          };
          protectDocument();
          new MutationObserver(protectDocument).observe(
            document.documentElement,
            { childList: true, subtree: true });
        })();
        """;
}
