using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace QuickSL.Core;

internal class DisconnectSuppressingNetGameService : DispatchProxy
{
    private INetGameService? inner;

    internal static INetGameService Create(INetGameService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        object proxy = Create(typeof(INetGameService), typeof(DisconnectSuppressingNetGameService));
        var instance = (DisconnectSuppressingNetGameService)proxy;
        instance.inner = inner;
        return (INetGameService)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        INetGameService target = inner
            ?? throw new InvalidOperationException("断线抑制网络代理尚未初始化。");

        if (targetMethod.Name == nameof(INetGameService.Disconnect))
        {
            object? reason = args is { Length: > 0 } ? args[0] : null;
            object? now = args is { Length: > 1 } ? args[1] : false;
            ModLogger.Debug(
                $"多人快速 SL：跳过 CleanUp 中的 NetService.Disconnect({reason}, now={now})。");
            return null;
        }

        try
        {
            // 运行时代理会按当前游戏实际的 INetGameService 形态转发全部成员。
            // 因此 0.107.1 不会静态引用尚不存在的 PeerVersionInfo，0.111 的 LocalVersion
            // 也会自动转发给原服务。
            return targetMethod.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
