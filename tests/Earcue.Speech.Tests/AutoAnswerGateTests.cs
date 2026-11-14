using Earcue.Core.Models;
using Earcue.Speech.Vad;

namespace Earcue.Speech.Tests;

public class AutoAnswerGateTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AudioFrame Frame(bool speech)
    {
        var pcm = new byte[320];
        if (speech)
            for (var i = 0; i < pcm.Length; i += 2) { pcm[i] = 0; pcm[i + 1] = 8; }
        return new AudioFrame(AudioRole.Remote, pcm, 16000, 1, 16, TimeSpan.Zero, !speech);
    }

    [Fact]
    public void ContinuingAudioBlocksAnswerEvenWhenRecognitionStopsChanging()
    {
        var gate = new AutoAnswerGate();
        gate.Reset(Start);
        gate.ObserveRecognition(true, Start);
        for (var ms = 0; ms <= 5000; ms += 10)
            gate.ObserveAudio(Frame(true), Start.AddMilliseconds(ms));
        Assert.False(gate.CanAnswer(Start.AddSeconds(5), 2000, out _));
    }

    [Fact]
    public void FinalizedSpeechRequiresACompleteQuietInterval()
    {
        var gate = new AutoAnswerGate();
        gate.Reset(Start);
        for (var ms = 0; ms <= 1000; ms += 10)
            gate.ObserveAudio(Frame(ms < 500), Start.AddMilliseconds(ms));
        gate.ObserveRecognition(true, Start.AddSeconds(1));
        gate.ObserveAudio(Frame(false), Start.AddMilliseconds(2999));
        Assert.False(gate.CanAnswer(Start.AddMilliseconds(2999), 2000, out _));
        Assert.True(gate.CanAnswer(Start.AddSeconds(3), 2000, out var quiet));
        Assert.Equal(TimeSpan.FromSeconds(2), quiet);
    }

    [Fact]
    public void UnfinalizedDraftBlocksAnswerUntilEndpointArrives()
    {
        var gate = new AutoAnswerGate();
        gate.Reset(Start);
        gate.ObserveRecognition(false, Start);
        Assert.False(gate.CanAnswer(Start.AddSeconds(10), 2000, out _));
        gate.ObserveRecognition(true, Start.AddSeconds(10));
        gate.ObserveAudio(Frame(false), Start.AddSeconds(12));
        Assert.True(gate.CanAnswer(Start.AddSeconds(12), 2000, out _));
    }

    [Fact]
    public void ShortSoundRestartsQuietIntervalAndResetClearsDraft()
    {
        var gate = new AutoAnswerGate();
        gate.Reset(Start);
        gate.ObserveAudio(Frame(true), Start.AddMilliseconds(1900));
        Assert.False(gate.CanAnswer(Start.AddSeconds(2), 2000, out _));
        gate.ObserveRecognition(false, Start.AddSeconds(2));
        gate.Reset(Start.AddSeconds(3));
        gate.ObserveAudio(Frame(false), Start.AddSeconds(5));
        Assert.True(gate.CanAnswer(Start.AddSeconds(5), 2000, out _));
    }

    [Fact]
    public void MissingAudioHeartbeatBlocksAnswerDuringCaptureInterruption()
    {
        var gate = new AutoAnswerGate();
        gate.Reset(Start);
        gate.ObserveRecognition(true, Start);
        gate.ObserveAudio(Frame(false), Start);
        Assert.False(gate.CanAnswer(Start.AddSeconds(3), 2000, out _));
        gate.ObserveAudio(Frame(false), Start.AddSeconds(3));
        Assert.True(gate.CanAnswer(Start.AddSeconds(3), 2000, out _));
    }
}
