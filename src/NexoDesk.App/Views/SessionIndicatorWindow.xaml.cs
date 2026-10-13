using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;

namespace NexoDesk.App.Views;

public partial class SessionIndicatorWindow : Window
{
    private readonly Action _endSession;
    private bool _allowClose;

    public SessionIndicatorWindow(Action endSession, AgentSession session)
    {
        InitializeComponent();
        _endSession = endSession;
        AdminRun.Text = "ADMIN " + session.ClientName;
        ModeRun.Text = session.Mode == SessionMode.Control ? "controlando" : "visualizando";

        Loaded += (_, _) => PlaceAtTopCenter();
        SizeChanged += (_, _) => PlaceAtTopCenter();
        // Pode ser arrastada na horizontal se cobrir algo importante.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.Source is not System.Windows.Controls.Button)
                DragMove();
        };
    }

    /// <summary>Fecha a faixa quando a sessão termina.</summary>
    public void CloseForSessionEnd()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Alt+F4 não esconde o aviso: só encerrar o acesso remove a faixa.
        if (!_allowClose)
            e.Cancel = true;
    }

    private void OnEnd(object sender, RoutedEventArgs e)
    {
        EndButton.IsEnabled = false;
        EndButton.Content = "Encerrando…";
        _endSession();
    }

    private void PlaceAtTopCenter()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top;
    }
}
