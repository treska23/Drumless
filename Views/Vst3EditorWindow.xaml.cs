using System.Windows;
using NAudio.Vst3;

namespace DrumPracticeStudio.Views;

public partial class Vst3EditorWindow : Window
{
    private readonly Vst3EditorHost _editorHost;

    public Vst3EditorWindow(string title, Vst3PluginView view)
    {
        InitializeComponent();
        Title = title;
        _editorHost = new Vst3EditorHost(view);
        EditorHostSlot.Content = _editorHost;
        Closed += OnClosed;
    }

    public event EventHandler? ClosedByUser;

    private void OnClosed(object? sender, EventArgs e)
    {
        _editorHost.Dispose();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }
}
