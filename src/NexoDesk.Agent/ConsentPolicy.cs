using System.Windows.Threading;
using NexoDesk.Shared.Messages;
using NexoDesk.Shared.Session;
using NexoDesk.Ui.Controls;

namespace NexoDesk.Agent;

/// <summary>
/// Política local: com a sessão bloqueada, recusa; com acesso não assistido desligado (padrão),
/// pergunta a quem está no computador e recusa sozinha quando o prazo acaba.
/// </summary>
public sealed class ConsentPolicy(Dispatcher dispatcher, SessionLockMonitor lockMonitor, bool unattendedAccess, TimeSpan consentTimeout) : IAgentPolicy
{
    public async Task<PolicyDecision> RequestSessionAsync(SessionRequest request, CancellationToken ct)
    {
        if (lockMonitor.IsLocked)
            return PolicyDecision.Deny(DenyReason.SessionUnavailable,
                "O computador remoto está bloqueado. O NexoDesk não acessa a tela de bloqueio nem a de login.");

        if (unattendedAccess)
            return PolicyDecision.Allow();

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NxDialogWindow? dialog = null;

        await dispatcher.InvokeAsync(() =>
        {
            string what = request.Mode == SessionMode.Control ? "ver e controlar" : "ver";
            var d = new NxDialogWindow(new DialogOptions
            {
                Kind = DialogKind.Warning,
                Title = "Permitir acesso remoto?",
                Text = $"{request.Client.Name} quer {what} a tela deste computador (endereço {request.RemoteAddress}). " +
                       "Enquanto o acesso estiver ativo, um aviso fica no topo da tela com o botão Encerrar acesso.",
                PrimaryText = "Permitir",
                SecondaryText = "Recusar",
                AutoCancelAfter = consentTimeout,
                CountdownFormat = left => $"Recusa automática em {Math.Ceiling(left.TotalSeconds):0} s.",
                Topmost = true,
            });
            d.Closed += (_, _) => answer.TrySetResult(d.Result);
            d.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            dialog = d;
            d.Show();
            d.Activate();
        });

        // Se o administrador desistir ou o prazo da política acabar, o diálogo fecha sozinho.
        await using (ct.Register(() => dispatcher.InvokeAsync(() => dialog?.Dismiss())))
        {
            bool allowed = await answer.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return allowed
                ? PolicyDecision.Allow()
                : PolicyDecision.Deny(DenyReason.ConsentDeclined, "A pessoa no computador remoto recusou o acesso ou não respondeu.");
        }
    }
}
