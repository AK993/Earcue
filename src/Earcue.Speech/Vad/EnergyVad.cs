using Earcue.Core.Models;

namespace Earcue.Speech.Vad;

/// <summary>
/// 能量与 RMS 语音活动检测器 (VAD)
/// 快速滤除静音帧及极短噪声（< 0.4s）
/// </summary>
public sealed class EnergyVad
{
    private readonly double _energyThreshold;
    private readonly int _minSpeechFrames; // 例如 40 帧 (400ms)
    private int _consecutiveSpeechFrames = 0;
    private int _consecutiveSilenceFrames = 0;

    public bool IsInSpeech { get; private set; }

    public EnergyVad(double energyThreshold = 300.0, int minSpeechFrames = 40)
    {
        _energyThreshold = energyThreshold;
        _minSpeechFrames = minSpeechFrames;
    }

    /// <summary>
    /// 处理单帧音频 (10ms)
    /// </summary>
    /// <returns>当前帧是否属于有效语音段</returns>
    public bool ProcessFrame(AudioFrame frame)
    {
        if (frame.IsSilence)
        {
            _consecutiveSpeechFrames = 0;
            _consecutiveSilenceFrames++;
            if (_consecutiveSilenceFrames > 15) // > 150ms 静音认为退出语音状态
            {
                IsInSpeech = false;
            }
            return false;
        }

        double rms = frame.CalculateRms();
        if (rms >= _energyThreshold)
        {
            _consecutiveSpeechFrames++;
            _consecutiveSilenceFrames = 0;
            if (_consecutiveSpeechFrames >= _minSpeechFrames)
            {
                IsInSpeech = true;
            }
        }
        else
        {
            _consecutiveSpeechFrames = 0;
            _consecutiveSilenceFrames++;
            if (_consecutiveSilenceFrames > 15)
            {
                IsInSpeech = false;
            }
        }

        return IsInSpeech;
    }

    public void Reset()
    {
        _consecutiveSpeechFrames = 0;
        _consecutiveSilenceFrames = 0;
        IsInSpeech = false;
    }
}
