using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NexoDesk.Ui.Controls;

public enum DialogKind
{
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>Opções do diálogo de mensagem. Textos em português, verbo no botão ("Permitir", não "OK").</summary>
public sealed record DialogOptions
{
    public DialogKind Kind { get; init; } = DialogKind.Info;
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string PrimaryText { get; init; }
    public string? SecondaryText { get; init; } = "Cancelar";
    public bool PrimaryIsDanger { get; init; }

    /// <summary>Fecha sozinho (como se fosse "secundário") depois desse tempo.</summary>
    public TimeSpan? AutoCancelAfter { get; init; }

    /// <summary>Texto da contagem regressiva; recebe o tempo restante.</summary>
    public Func<TimeSpan, string>? CountdownFormat { get; init; }

    public bool Topmost { get; init; }
}

/// <summary>Diálogo de mensagem do Nexotool UI (ícone em círculo, título, texto e rodapé com botões à direita).</summary>
public partial class NxDialogWindow : Window
{
    private readonly DialogOptions _options;
    private readonly DispatcherTimer? _timer;
    private DateTime _deadline;
    private bool _primary;

    public NxDialogWindow(DialogOptions options)
    {
        InitializeComponent();
        _options = options;

        TitleText.Text = options.Title;
        BodyText.Text = options.Text;
        PrimaryButton.Content = options.PrimaryText;
        Topmost = options.Topmost;

        if (options.SecondaryText is null)
            SecondaryButton.Visibility = Visibility.Collapsed;
        else
            SecondaryButton.Content = options.SecondaryText;

        if (options.PrimaryIsDanger)
            PrimaryButton.SetResourceReference(StyleProperty, "Nx.Button.DangerSolid");

        (string circle, string fore, string glyph) = options.Kind switch
        {
            DialogKind.Success => ("Nx.SuccessSoft", "Nx.Success", ""),
            DialogKind.Warning => ("Nx.WarningSoft", "Nx.Warning", ""),
            DialogKind.Danger => ("Nx.DangerSoft", "Nx.Danger", ""),
            _ => ("Nx.BlueSoft", "Nx.Blue", ""),
        };
        IconCircle.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, circle);
        IconGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, fore);
        IconGlyph.Text = glyph;

        if (options.AutoCancelAfter is TimeSpan limit)
        {
            _deadline = DateTime.UtcNow + limit;
            CountdownText.Visibility = Visibility.Visible;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Tick();
            Loaded += (_, _) => { Tick(); _timer.Start(); };
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        Closed += (_, _) => _timer?.Stop();
    }

    /// <summary>Mostra o diálogo de forma modal. Retorna true se o botão principal foi escolhido.</summary>
    public static bool Ask(Window? owner, DialogOptions options)
    {
        var dialog = new NxDialogWindow(options);
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._primary;
    }

    /// <summary>True se o botão principal foi escolhido (válido depois de fechado; útil com Show() não modal).</summary>
    public bool Result => _primary;

    /// <summary>Fecha o diálogo como "secundário" (ex.: a conexão foi cancelada enquanto o pedido estava aberto).</summary>
    public void Dismiss()
    {
        _primary = false;
        if (IsLoaded)
            Close();
    }

    private void Tick()
    {
        TimeSpan left = _deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            Dismiss();
            return;
        }
        CountdownText.Text = _options.CountdownFormat?.Invoke(left) ?? $"Fecha sozinho em {Math.Ceiling(left.TotalSeconds):0} s.";
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        _primary = true;
        Close();
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        _primary = false;
        Close();
    }
}
