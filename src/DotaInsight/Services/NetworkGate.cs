namespace DotaInsight.Services;

/// <summary>
/// 网络占用闸：标记「用户主动操作」的进行区间。
/// 后台预热在区间内让路，避免与用户查询抢同一条本就脆弱的线路。
/// </summary>
public interface INetworkGate
{
    /// <summary>是否有用户主动操作正在进行。</summary>
    bool IsUserBusy { get; }

    /// <summary>进入用户操作区间（Dispose 时退出）。典型用法：using 包住一次用户触发的查询。</summary>
    IDisposable BeginUserOperation();
}

public sealed class NetworkGate : INetworkGate
{
    private int _active;

    public bool IsUserBusy => Volatile.Read(ref _active) > 0;

    public IDisposable BeginUserOperation()
    {
        Interlocked.Increment(ref _active);
        return new Releaser(this);
    }

    private sealed class Releaser(NetworkGate gate) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref gate._active);
    }
}
