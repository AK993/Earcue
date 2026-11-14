using Earcue.Core.Models;

namespace Earcue.Speech.Vad;

/// <summary>Requires both quiet audio and finalized, settled recognition before answering.</summary>
public sealed class AutoAnswerGate
{
    private readonly object _gate = new();
    private readonly EnergyVad _vad = new(minSpeechFrames: 3);
    private DateTime _lastActivity;
    private DateTime _lastAudio;
    private bool _hasDraft;

    public void Reset(DateTime now)
    {
        lock (_gate)
        {
            _vad.Reset();
            _lastActivity = now;
            _lastAudio = DateTime.MinValue;
            _hasDraft = false;
        }
    }

    public void ObserveAudio(AudioFrame frame, DateTime now)
    {
        lock (_gate)
        {
            _lastAudio = now;
            // Short bursts also restart the quiet interval; sustained activity gets VAD hangover.
            var voiced = !frame.IsSilence && frame.CalculateRms() >= 300;
            _vad.ProcessFrame(frame);
            if (voiced || _vad.IsInSpeech)
                _lastActivity = now;
        }
    }

    public void ObserveRecognition(bool isFinal, DateTime now)
    {
        lock (_gate)
        {
            _hasDraft = !isFinal;
            _lastActivity = now;
        }
    }

    public bool CanAnswer(DateTime now, int silenceMs, out TimeSpan quiet)
    {
        lock (_gate)
        {
            quiet = now - _lastActivity;
            return _lastAudio != DateTime.MinValue && (now - _lastAudio).TotalSeconds <= 1 &&
                   !_hasDraft && !_vad.IsInSpeech &&
                   quiet.TotalMilliseconds >= Math.Max(500, silenceMs);
        }
    }
}
