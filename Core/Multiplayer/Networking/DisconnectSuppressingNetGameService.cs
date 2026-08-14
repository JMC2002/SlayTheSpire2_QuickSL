using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace QuickSL.Core;

internal static class DisconnectSuppressingNetGameService
{
    internal static INetGameService Create(INetGameService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return (INetGameService)RuntimeInterfaceProxy.Create(
            typeof(INetGameService),
            (targetMethod, args) => Invoke(inner, targetMethod, args));
    }

    private static object? Invoke(
        INetGameService target,
        MethodInfo targetMethod,
        object?[]? args)
    {
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
            // 自建代理会按当前游戏实际的 INetGameService 形态转发全部成员。
            // 因此 0.107.1 不会静态引用尚不存在的 PeerVersionInfo，0.111 的 LocalVersion
            // 也会自动转发给原服务，同时不依赖可能包含安装路径的加载上下文名称。
            return targetMethod.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
