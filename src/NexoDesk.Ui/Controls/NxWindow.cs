using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace NexoDesk.Ui.Controls;

/// <summary>
/// Janela padrão Nexotool: barra de título azul-marinho de 36 px (que também é a barra da janela),
/// linha em degradê de 2 px, área de conteúdo sobre fundo cinza-azulado e rodapé azul-marinho de 34 px.
/// </summary>
public class NxWindow : Window
{
    public static readonly DependencyProperty TaglineProperty =
        DependencyProperty.Register(nameof(Tagline), typeof(string), typeof(NxWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleBarContentProperty =
        DependencyProperty.Register(nameof(TitleBarContent), typeof(object), typeof(NxWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterContentProperty =
        DependencyProperty.Register(nameof(FooterContent), typeof(object), typeof(NxWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterRightContentProperty =
        DependencyProperty.Register(nameof(FooterRightContent), typeof(object), typeof(NxWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowFooterProperty =
        DependencyProperty.Register(nameof(ShowFooter), typeof(bool), typeof(NxWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty CanMinimizeProperty =
        DependencyProperty.Register(nameof(CanMinimize), typeof(bool), typeof(NxWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty CanMaximizeProperty =
        DependencyProperty.Register(nameof(CanMaximize), typeof(bool), typeof(NxWindow), new PropertyMetadata(true));

    static NxWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NxWindow), new FrameworkPropertyMetadata(typeof(NxWindow)));
    }

    public NxWindow()
    {
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
    }

    /// <summary>Texto após o nome na barra de título ("· Verificação de bancos Access").</summary>
    public string? Tagline
    {
        get => (string?)GetValue(TaglineProperty);
        set => SetValue(TaglineProperty, value);
    }

    /// <summary>Ações na barra de título (pílula de estado, botões azul-marinho compactos).</summary>
    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    /// <summary>Texto à esquerda no rodapé (produto e versão).</summary>
    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    /// <summary>Links à direita no rodapé.</summary>
    public object? FooterRightContent
    {
        get => GetValue(FooterRightContentProperty);
        set => SetValue(FooterRightContentProperty, value);
    }

    public bool ShowFooter
    {
        get => (bool)GetValue(ShowFooterProperty);
        set => SetValue(ShowFooterProperty, value);
    }

    public bool CanMinimize
    {
        get => (bool)GetValue(CanMinimizeProperty);
        set => SetValue(CanMinimizeProperty, value);
    }

    public bool CanMaximize
    {
        get => (bool)GetValue(CanMaximizeProperty);
        set => SetValue(CanMaximizeProperty, value);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizedMargin();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateMaximizedMargin();
    }

    /// <summary>Janela sem moldura maximizada passa da tela pela espessura da borda de redimensionamento; compensa aqui.</summary>
    private void UpdateMaximizedMargin()
    {
        if (GetTemplateChild("PART_Root") is not Border root)
            return;

        if (WindowState == WindowState.Maximized)
        {
            Thickness t = SystemParameters.WindowResizeBorderThickness;
            Thickness pad = WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness ?? new Thickness(0);
            root.Margin = new Thickness(
                Math.Max(t.Left, pad.Left) + 1, Math.Max(t.Top, pad.Top) + 1,
                Math.Max(t.Right, pad.Right) + 1, Math.Max(t.Bottom, pad.Bottom) + 1);
            root.BorderThickness = new Thickness(0);
        }
        else
        {
            root.Margin = new Thickness(0);
            root.BorderThickness = new Thickness(1);
        }
    }
}
