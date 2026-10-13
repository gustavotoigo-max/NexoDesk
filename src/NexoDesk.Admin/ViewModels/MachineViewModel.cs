using NexoDesk.Shared.Configuration;
using NexoDesk.Ui;
using NexoDesk.Ui.Controls;

namespace NexoDesk.Admin.ViewModels;

public enum MachineStatus
{
    Unknown,
    Checking,
    Available,
    Unavailable,
    Connecting,
    InSession,
}

/// <summary>Linha da tabela de computadores. "Disponível" só aparece com evidência real (sondagem bem-sucedida).</summary>
public sealed class MachineViewModel(MachineEntry entry) : ObservableObject
{
    private MachineStatus _status = MachineStatus.Unknown;
    private DateTime? _lastCheck;
    private string? _detail;
    private TimeSpan? _latency;

    public MachineEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string DisplayName => Entry.DisplayName;
    public string Address => $"{Entry.Host}:{Entry.Port}";

    public MachineStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusKind));
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public string StatusText => Status switch
    {
        MachineStatus.Checking => "Verificando",
        MachineStatus.Available => "Disponível",
        MachineStatus.Unavailable => "Indisponível",
        MachineStatus.Connecting => "Conectando",
        MachineStatus.InSession => "Em sessão",
        _ => "Desconhecido",
    };

    public BadgeKind StatusKind => Status switch
    {
        MachineStatus.Available => BadgeKind.Success,
        MachineStatus.Unavailable => BadgeKind.Danger,
        MachineStatus.Checking or MachineStatus.Connecting => BadgeKind.Warning,
        MachineStatus.InSession => BadgeKind.Info,
        _ => BadgeKind.Neutral,
    };

    public bool IsIdle => Status is not (MachineStatus.Checking or MachineStatus.Connecting or MachineStatus.InSession);

    public DateTime? LastCheck
    {
        get => _lastCheck;
        set
        {
            if (Set(ref _lastCheck, value))
                OnPropertyChanged(nameof(LastCheckText));
        }
    }

    public string LastCheckText => LastCheck is DateTime t ? t.ToString("HH:mm:ss") : "—";

    /// <summary>Motivo da última falha, mostrado como dica na linha.</summary>
    public string? Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public TimeSpan? Latency
    {
        get => _latency;
        set
        {
            if (Set(ref _latency, value))
                OnPropertyChanged(nameof(LatencyText));
        }
    }

    public string LatencyText => Latency is TimeSpan l ? $"{l.TotalMilliseconds:0} ms" : "—";
}
