using System.Windows;
using System.Windows.Controls;

namespace NexoDesk.Ui.Controls;

public enum BadgeKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Info,
}

/// <summary>Selo de estado (OK, Erro, Em andamento…). Raio 10, texto 11,5 px semibold.</summary>
public class NxBadge : ContentControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(BadgeKind), typeof(NxBadge), new PropertyMetadata(BadgeKind.Neutral));

    public static readonly DependencyProperty ShowDotProperty =
        DependencyProperty.Register(nameof(ShowDot), typeof(bool), typeof(NxBadge), new PropertyMetadata(false));

    static NxBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NxBadge), new FrameworkPropertyMetadata(typeof(NxBadge)));
    }

    public BadgeKind Kind
    {
        get => (BadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Mostra um ponto colorido antes do texto.</summary>
    public bool ShowDot
    {
        get => (bool)GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }
}

/// <summary>Pílula de estado para a barra de título e cabeçalhos ("Licença ativa", "Sessão ativa").</summary>
public class NxPill : ContentControl
{
    static NxPill()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NxPill), new FrameworkPropertyMetadata(typeof(NxPill)));
    }
}
