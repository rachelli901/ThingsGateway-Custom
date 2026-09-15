//------------------------------------------------------------------------------
//  此代码版权声明为全文件覆盖，如有原作者特别声明，会在下方手动补充
//  此代码版权（除特别声明外的代码）归作者本人Diego所有
//  源代码使用协议遵循本仓库的开源协议及附加协议
//  Gitee源代码仓库：https://gitee.com/diego2098/ThingsGateway
//  Github源代码仓库：https://github.com/kimdiego2098/ThingsGateway
//  使用文档：https://thingsgateway.cn/
//  QQ群：605534569
//------------------------------------------------------------------------------

using TouchSocket.Core;


namespace ThingsGateway.Gateway.Application;

public class AsyncReadWriteLock : IDisposable
{
    private readonly int _writeReadRatio = 3; // 写3次会允许1次读，但写入也不会被阻止，具体协议取决于插件协议实现
    private readonly bool _writePriority;
    private readonly object _lockObject = new();
    private readonly object _leaseGateLock = new();
    private readonly AsyncAutoResetEvent _readerGate = new(false); // 旧通道：控制读计数
    private readonly AsyncAutoResetEvent _readerLeaseGate = new(false); // 新通道：控制 ReaderLease 读者入口
    private readonly AsyncAutoResetEvent _writerLeaseGate = new(false); // 新通道：ReaderLease 读者退出通知写者
    private long _writerCount; // 当前活跃的写线程数
    private long _readerCount; // 当前被阻塞的旧通道读线程数
    private long _readerLeaseCount; // 当前活跃的 ReaderLease 读者数
    private long _readerGeneration; // ReaderLease 读者的世代号
    private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    public AsyncReadWriteLock(int writeReadRatio, bool writePriority)
    {
        _writeReadRatio = writeReadRatio;
        _writePriority = writePriority;
    }

    /// <summary>
    /// 获取读锁，支持多个线程并发读取，但写入时会阻止所有读取。
    /// 该接口保留原有取消语义，供兼容调用方使用。
    /// </summary>
    public ValueTask<CancellationToken> ReaderLockAsync(CancellationToken cancellationToken)
    {
        return ReaderLockAsync(this, cancellationToken);
        static async PooledValueTask<CancellationToken> ReaderLockAsync(
        AsyncReadWriteLock @this,
        CancellationToken cancellationToken)
        {
            if (Interlocked.Read(ref @this._writerCount) > 0)
            {

                Task task = null;
                lock (@this._lockObject)
                {
                    //AsyncAutoResetEvent加入队列是同步的，所以不会担心并发task未等待导致的问题
                    if (Interlocked.Read(ref @this._writerCount) > 0)
                        task = @this._readerGate.WaitOneAsync(cancellationToken);
                }

                if (task != null)
                {
                    Interlocked.Increment(ref @this._readerCount);
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref @this._readerCount);
                    }
                }

            }

            return @this._cancellationTokenSource.Token;
        }
    }

    /// <summary>
    /// 获取 ReaderLease 读锁。写者抢占不会取消调用方令牌，写者会等待已获取 Lease 的读者释放。
    /// </summary>
    internal ValueTask<ReaderLease> ReaderLeaseLockAsync(CancellationToken cancellationToken)
    {
        return ReaderLeaseLockAsync(this, cancellationToken);

        static async PooledValueTask<ReaderLease> ReaderLeaseLockAsync(
        AsyncReadWriteLock @this,
        CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Interlocked.Read(ref @this._writerCount) == 0)
                {
                    var generation = Interlocked.Read(ref @this._readerGeneration);
                    Interlocked.Increment(ref @this._readerLeaseCount);

                    // 读者进入与写者进入并发时，以 writerCount 的二次检查确定先后顺序。
                    // 如果写者已经抢占，读者立即归还计数并等待，不执行实际读取。
                    if (Interlocked.Read(ref @this._writerCount) == 0)
                    {
                        return new ReaderLease(@this, generation, cancellationToken);
                    }

                    @this.ReleaseReaderLease();
                }

                Task task = null;
                lock (@this._leaseGateLock)
                {
                    if (Interlocked.Read(ref @this._writerCount) > 0)
                        task = @this._readerLeaseGate.WaitOneAsync(cancellationToken);
                }

                if (task != null)
                {
                    await task.ConfigureAwait(false);
                }
            }
        }
    }

    public bool WriteWaited => _writerCount > 0;
    public bool ReadWaited => _readerCount > 0;

    /// <summary>
    /// 获取写锁，阻止所有读取。
    /// </summary>
    public ValueTask<IDisposable> WriterLockAsync()
    {
        return WriterLockAsync(this);

        static async PooledValueTask<IDisposable> WriterLockAsync(AsyncReadWriteLock @this)
        {
            if (Interlocked.Increment(ref @this._writerCount) == 1)
            {
                if (@this._writePriority)
                {
                    Interlocked.Increment(ref @this._readerGeneration);

                    var cancellationTokenSource = @this._cancellationTokenSource;
                    @this._cancellationTokenSource = new();
                    await cancellationTokenSource.SafeCancelAsync().ConfigureAwait(false); // 只取消旧通道读取
                    cancellationTokenSource.SafeDispose();
                }
            }

            if (@this._writePriority)
            {
                await @this.WaitForReaderLeaseExitAsync().ConfigureAwait(false);
            }

            return new Writer(@this);
        }
    }

    private ValueTask WaitForReaderLeaseExitAsync()
    {
        return WaitForReaderLeaseExitAsync(this);

        static async PooledValueTask WaitForReaderLeaseExitAsync(AsyncReadWriteLock @this)
        {
            while (Interlocked.Read(ref @this._readerLeaseCount) > 0)
            {
                Task task = null;
                lock (@this._leaseGateLock)
                {
                    if (Interlocked.Read(ref @this._readerLeaseCount) > 0)
                        task = @this._writerLeaseGate.WaitOneAsync();
                }

                if (task != null)
                {
                    await task.ConfigureAwait(false);
                }
            }
        }
    }

    private void ReleaseReaderLease()
    {
        if (Interlocked.Decrement(ref _readerLeaseCount) == 0)
        {
            lock (_leaseGateLock)
            {
                // SetAll 在没有等待者时不会留下信号状态。
                _writerLeaseGate.SetAll();
            }
        }
    }

    private bool IsPreemptionRequested(long generation)
    {
        return _writePriority && generation < Interlocked.Read(ref _readerGeneration);
    }

    private void ReleaseWriter()
    {
        var writerCount = Interlocked.Decrement(ref _writerCount);
        var wakeReaderLease = writerCount == 0;

        lock (_lockObject)
        {
            if (writerCount == 0)
            {
                _writeSinceLastReadCount = 0;

                _readerGate.SetAll();
            }
            else
            {


                // 读写占空比， 用于控制写操作与读操作的比率。该比率 n 次写入操作会执行一次读取操作。即使在应用程序执行大量的连续写入操作时，也必须确保足够的读取数据处理时间。相对于更加均衡的读写数据流而言，该特点使得外部写入可连续无顾忌操作
                if (_writeReadRatio > 0)
                {

                    var count = _writeSinceLastReadCount++;
                    if (count >= _writeReadRatio)
                    {
                        _writeSinceLastReadCount = 0;
                        _readerGate.Set();
                    }
                }
                else
                {
                    _readerGate.Set();
                }
            }

        }

        if (wakeReaderLease)
        {
            lock (_leaseGateLock)
            {
                _readerLeaseGate.SetAll();
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.SafeCancel();
                _cancellationTokenSource.SafeDispose();
            }
            _readerGate.SetAll();
        }

        lock (_leaseGateLock)
        {
            _readerLeaseGate.SetAll();
            _writerLeaseGate.SetAll();
        }
    }

    private int _writeSinceLastReadCount = 0;

    internal readonly struct ReaderLease : IDisposable
    {
        private readonly AsyncReadWriteLock? _owner;
        private readonly long _generation;

        internal ReaderLease(AsyncReadWriteLock owner, long generation, CancellationToken callerToken)
        {
            _owner = owner;
            _generation = generation;
            CallerToken = callerToken;
        }

        public CancellationToken CallerToken { get; }

        public bool IsPreemptionRequested => _owner?.IsPreemptionRequested(_generation) == true;

        public void Dispose()
        {
            _owner?.ReleaseReaderLease();
        }
    }

    private struct Writer : IDisposable
    {
        private readonly AsyncReadWriteLock _lock;

        public Writer(AsyncReadWriteLock lockObj)
        {
            _lock = lockObj;
        }

        public void Dispose()
        {
            _lock.ReleaseWriter();
        }
    }
}
