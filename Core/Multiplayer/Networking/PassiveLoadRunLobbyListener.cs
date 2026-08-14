using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using System.Reflection;

namespace QuickSL.Core;

internal static class PassiveLoadRunLobbyListener
{
    private const string ListenerTypeName =
        "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.ILoadRunLobbyListener";

    internal static object CreateListener()
    {
        // 0.107.1–0.109.1 的 PlayerConnected 参数是 ulong；0.110 改为 LoadRunLobbyPlayer。
        // 单个静态类型无法同时实现两种接口形态，因此按当前游戏 DLL 动态生成接口代理。
        Type listenerType = typeof(LoadRunLobby).Assembly.GetType(ListenerTypeName, throwOnError: true)!;
        return RuntimeInterfaceProxy.Create(listenerType, Invoke);
    }

    private static object? Invoke(MethodInfo targetMethod, object?[]? args)
    {
        args ??= [];

        switch (targetMethod.Name)
        {
            case "PlayerConnected":
                ModLogger.Debug(
                    $"多人快速 SL：LoadRunLobby 玩家已连接 {QuickSlLobbyCompat.GetPlayerId(args[0])}。");
                return null;

            case "RemotePlayerDisconnected":
                ModLogger.Debug($"多人快速 SL：LoadRunLobby 玩家已断开 {(ulong)args[0]!}。");
                return null;

            case "ShouldAllowRunToBegin":
                return Task.FromResult(true);

            case "BeginRun":
                ModLogger.Debug("多人快速 SL：LoadRunLobby 收到开始载入通知。");
                return null;

            case "PlayerReadyChanged":
                ModLogger.Debug($"多人快速 SL：LoadRunLobby 玩家准备状态变化 {(ulong)args[0]!}。");
                return null;

            case "LocalPlayerDisconnected":
                var info = (NetErrorInfo)args[0]!;
                ModLogger.Warn($"多人快速 SL：LoadRunLobby 本地连接断开，原因={info.GetReason()}。");
                return null;

            default:
                throw new MissingMethodException(
                    $"QuickSL 尚未适配 {ListenerTypeName}.{targetMethod.Name} 回调。");
        }
    }
}
