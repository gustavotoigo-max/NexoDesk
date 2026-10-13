namespace NexoDesk.Shared.Protocol;

/// <summary>Códigos de erro enviados na mensagem Error e usados nos logs. Mensagens não sensíveis.</summary>
public enum ErrorCode
{
    None = 0,
    BadMagic = 1,
    UnsupportedVersion = 2,
    UnknownMessageType = 3,
    PayloadTooLarge = 4,
    MalformedPayload = 5,
    UnexpectedMessage = 6,
    HandshakeTimeout = 7,
    IdleTimeout = 8,
    NotAuthorized = 9,
    SessionBusy = 10,
    SessionUnavailable = 11,
    InternalError = 12,
    ConnectionClosed = 13,
}

public static class ErrorCodeText
{
    /// <summary>Texto curto em português, seguro para exibir ao usuário e gravar em log.</summary>
    public static string Describe(this ErrorCode code) => code switch
    {
        ErrorCode.None => "Sem erro.",
        ErrorCode.BadMagic => "Os dados recebidos não são do protocolo NexoDesk.",
        ErrorCode.UnsupportedVersion => "Versão do protocolo não suportada. Atualize o Admin e o Agent para a mesma versão.",
        ErrorCode.UnknownMessageType => "Tipo de mensagem desconhecido.",
        ErrorCode.PayloadTooLarge => "Mensagem acima do tamanho permitido.",
        ErrorCode.MalformedPayload => "Mensagem com conteúdo inválido.",
        ErrorCode.UnexpectedMessage => "Mensagem fora de ordem para o estado da sessão.",
        ErrorCode.HandshakeTimeout => "O outro lado não concluiu a negociação a tempo.",
        ErrorCode.IdleTimeout => "Sessão encerrada por inatividade da conexão.",
        ErrorCode.NotAuthorized => "Este administrador não está autorizado neste computador.",
        ErrorCode.SessionBusy => "Já existe uma sessão ativa neste computador.",
        ErrorCode.SessionUnavailable => "Não há sessão de usuário disponível no computador remoto.",
        ErrorCode.InternalError => "Erro interno. Consulte o log do computador remoto.",
        ErrorCode.ConnectionClosed => "A conexão foi encerrada.",
        _ => "Erro desconhecido.",
    };
}
