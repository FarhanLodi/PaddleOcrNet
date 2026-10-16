namespace PaddleOcrNet.Internal.Recognition;

/// <summary>Call-local tensor storage. No pixels or arrays survive the owning recognition call.</summary>
internal sealed class BoundedTensorWorkspace(int capacity, int elementsPerBuffer) : IDisposable
{
    private readonly object _gate = new();
    private readonly Stack<float[]> _idle = new();
    private int _allocated;
    private bool _disposed;

    internal Lease Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (length > elementsPerBuffer) throw new ArgumentOutOfRangeException(nameof(length));
        float[] buffer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_idle.Count > 0) buffer = _idle.Pop();
            else
            {
                if (_allocated >= capacity) throw new InvalidOperationException("Tensor workspace concurrency exceeded its capacity.");
                buffer = new float[elementsPerBuffer];
                _allocated++;
            }
        }
        // Always clear the complete active tensor, including padding and slots reused after shorter batches.
        buffer.AsSpan(0, length).Clear();
        return new Lease(this, buffer, length);
    }

    private void Return(float[] buffer)
    {
        lock (_gate)
            if (!_disposed) _idle.Push(buffer);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _idle.Clear();
        }
    }

    internal sealed class Lease(BoundedTensorWorkspace owner, float[] buffer, int length) : IDisposable
    {
        private float[]? _buffer = buffer;
        internal Memory<float> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(Lease))).AsMemory(0, length);
        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref _buffer, null);
            if (returned is not null) owner.Return(returned);
        }
    }
}
