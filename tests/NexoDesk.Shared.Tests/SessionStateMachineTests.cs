using NexoDesk.Shared.Protocol;
using NexoDesk.Shared.Session;

namespace NexoDesk.Shared.Tests;

public class SessionStateMachineTests
{
    [Fact]
    public void HappyPath_FollowsDocumentedOrder()
    {
        var sm = new SessionStateMachine(SessionRole.Admin);
        var seen = new List<SessionState>();
        sm.StateChanged += (_, next) => seen.Add(next);

        sm.TransitionTo(SessionState.Connecting);
        sm.TransitionTo(SessionState.Authenticated);
        sm.TransitionTo(SessionState.Negotiating);
        sm.TransitionTo(SessionState.Streaming);
        Assert.True(sm.TryBeginClose());
        sm.MarkClosed();

        Assert.Equal(
            new[] { SessionState.Connecting, SessionState.Authenticated, SessionState.Negotiating, SessionState.Streaming, SessionState.Closing, SessionState.Disconnected },
            seen);
    }

    [Fact]
    public void SkippingStates_Throws()
    {
        var sm = new SessionStateMachine(SessionRole.Agent);
        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(SessionState.Streaming));
        sm.TransitionTo(SessionState.Connecting);
        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(SessionState.Negotiating));
    }

    [Fact]
    public void Close_IsIdempotent()
    {
        var sm = new SessionStateMachine(SessionRole.Agent);
        Assert.False(sm.TryBeginClose()); // já desconectado

        sm.TransitionTo(SessionState.Connecting);
        Assert.True(sm.TryBeginClose());
        Assert.False(sm.TryBeginClose());
        sm.MarkClosed();
        sm.MarkClosed();
        Assert.Equal(SessionState.Disconnected, sm.State);
    }

    [Theory]
    [InlineData(SessionState.Authenticated, MessageType.Hello, true)]
    [InlineData(SessionState.Authenticated, MessageType.StartSession, false)]
    [InlineData(SessionState.Authenticated, MessageType.PointerEvent, false)]
    [InlineData(SessionState.Negotiating, MessageType.StartSession, true)]
    [InlineData(SessionState.Negotiating, MessageType.KeyEvent, false)]
    [InlineData(SessionState.Streaming, MessageType.KeyEvent, true)]
    [InlineData(SessionState.Streaming, MessageType.Hello, false)]
    [InlineData(SessionState.Streaming, MessageType.VideoFrame, false)]
    public void Agent_IncomingRules(SessionState state, MessageType type, bool allowed)
    {
        Assert.Equal(allowed, SessionStateMachine.IsIncomingAllowed(SessionRole.Agent, state, type));
    }

    [Theory]
    [InlineData(SessionState.Authenticated, MessageType.HelloAck, true)]
    [InlineData(SessionState.Authenticated, MessageType.VideoFrame, false)]
    [InlineData(SessionState.Negotiating, MessageType.SessionAccepted, true)]
    [InlineData(SessionState.Negotiating, MessageType.VideoFrame, false)]
    [InlineData(SessionState.Streaming, MessageType.VideoFrame, true)]
    [InlineData(SessionState.Streaming, MessageType.PointerEvent, false)]
    public void Admin_IncomingRules(SessionState state, MessageType type, bool allowed)
    {
        Assert.Equal(allowed, SessionStateMachine.IsIncomingAllowed(SessionRole.Admin, state, type));
    }

    [Fact]
    public void EnsureIncomingAllowed_ThrowsUnexpectedMessage()
    {
        var sm = new SessionStateMachine(SessionRole.Agent);
        sm.TransitionTo(SessionState.Connecting);
        sm.TransitionTo(SessionState.Authenticated);
        var ex = Assert.Throws<ProtocolException>(() => sm.EnsureIncomingAllowed(MessageType.KeyEvent));
        Assert.Equal(ErrorCode.UnexpectedMessage, ex.Code);
    }

    [Fact]
    public void SessionGate_AllowsOnlyOneSession()
    {
        var gate = new SessionGate();
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());
        gate.Release();
        Assert.True(gate.TryAcquire());
    }
}
