using System.ComponentModel;
using KodaMemo.ViewModels;

namespace KodaMemo;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly MicrophoneLevelDrawable _level = new();

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        AudioLevelView.Drawable = _level;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        await _viewModel.InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDisappearing();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.RmsLevel)) return;
        _level.Add(_viewModel.RmsLevel);
        AudioLevelView.Invalidate();
    }
}

/// <summary>Displays actual RMS readings, rather than a decorative simulated waveform.</summary>
internal sealed class MicrophoneLevelDrawable : IDrawable
{
    private readonly Queue<float> _levels = new();
    public void Add(double level)
    {
        _levels.Enqueue((float)Math.Clamp(level, 0, 1));
        while (_levels.Count > 48) _levels.Dequeue();
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var levels = _levels.ToArray();
        float step = dirtyRect.Width / 48;
        float width = Math.Max(2, step - 3);
        for (int i = 0; i < 48; i++)
        {
            int index = i - (48 - levels.Length);
            float value = index < 0 ? 0 : levels[index];
            float height = Math.Max(3, MathF.Sqrt(value) * (dirtyRect.Height - 8));
            canvas.FillColor = value > 0.5f ? Color.FromArgb("#F25F4C") : Color.FromArgb("#81CBC5");
            canvas.FillRoundedRectangle(i * step, (dirtyRect.Height - height) / 2, width, height, 2);
        }
    }
}
