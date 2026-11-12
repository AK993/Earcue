namespace Earcue.Audio.Processing;

/// <summary>
/// 专用于音频采集的单写单读环形缓冲区
/// 音频回调中写入时不触发内存分配与阻塞
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private int _writeIndex;
    private int _readIndex;
    private readonly object _lock = new();

    public AudioRingBuffer(int capacityBytes = 1024 * 1024) // 默认 1MB 缓冲区
    {
        _capacity = capacityBytes;
        _buffer = new byte[capacityBytes];
    }

    public int AvailableToRead
    {
        get
        {
            lock (_lock)
            {
                if (_writeIndex >= _readIndex)
                    return _writeIndex - _readIndex;
                return _capacity - _readIndex + _writeIndex;
            }
        }
    }

    public bool Write(byte[] data, int offset, int count)
    {
        if (count <= 0 || count > _capacity) return false;

        lock (_lock)
        {
            int freeSpace = _capacity - AvailableToRead - 1;
            if (freeSpace < count)
            {
                // 空间不足，发生溢出（可按需丢弃最老数据或拒绝写入）
                return false;
            }

            int firstPart = Math.Min(count, _capacity - _writeIndex);
            Buffer.BlockCopy(data, offset, _buffer, _writeIndex, firstPart);

            int secondPart = count - firstPart;
            if (secondPart > 0)
            {
                Buffer.BlockCopy(data, offset + firstPart, _buffer, 0, secondPart);
            }

            _writeIndex = (_writeIndex + count) % _capacity;
            return true;
        }
    }

    public int Read(byte[] destination, int offset, int count)
    {
        lock (_lock)
        {
            int available = AvailableToRead;
            int toRead = Math.Min(count, available);
            if (toRead <= 0) return 0;

            int firstPart = Math.Min(toRead, _capacity - _readIndex);
            Buffer.BlockCopy(_buffer, _readIndex, destination, offset, firstPart);

            int secondPart = toRead - firstPart;
            if (secondPart > 0)
            {
                Buffer.BlockCopy(_buffer, 0, destination, offset + firstPart, secondPart);
            }

            _readIndex = (_readIndex + toRead) % _capacity;
            return toRead;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writeIndex = 0;
            _readIndex = 0;
        }
    }
}
