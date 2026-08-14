using JmcModLib.Compat;
using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Collections;

namespace QuickSL.Core;

internal readonly record struct QuickSlLobbyPlayerState(
    object? VersionInfo,
    bool? IsModded);

internal static class QuickSlLobbyCompat
{
    // 0.107.1–0.109.1 的 RunLobby 保存 HashSet<ulong> _connectedPlayerIds；
    // 0.110 改为公开 List<RunLobbyPlayer> Players，并在玩家对象中附带版本信息；
    // 0.111 将版本信息收窄为 isModded。
    private static readonly Lazy<MemberAccessor?> RunLobbyPlayersAccessor = new(() =>
        FindReadableMember(typeof(RunLobby), "Players", "_connectedPlayerIds"));

    // 0.107.1–0.109.1 的 LoadRunLobby 公开 HashSet<ulong> ConnectedPlayerIds；
    // 0.110 改为 List<LoadRunLobbyPlayer> Players，创建玩家时必须提供 PeerVersionInfo；
    // 0.111 改为提供 isModded。
    private static readonly Lazy<MemberAccessor?> LoadRunLobbyPlayersAccessor = new(() =>
        FindReadableMember(typeof(LoadRunLobby), "Players", "ConnectedPlayerIds"));

    internal static LoadRunLobby CreateLoadRunLobby(
        INetGameService netService,
        SerializableRun runSave,
        object listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return TypeAccessor.Get(typeof(LoadRunLobby))
                   .CreateInstance(netService, listener, runSave) as LoadRunLobby
               ?? throw new InvalidOperationException("无法创建兼容当前游戏版本的 LoadRunLobby。");
    }

    internal static IReadOnlyDictionary<ulong, QuickSlLobbyPlayerState> CaptureRunLobbyPlayerStates(
        RunLobby? lobby)
    {
        var playerStateById = MultiplayerCompat.GetRunLobbyPlayerIds(lobby)
            .ToDictionary(
                static playerId => playerId,
                static _ => default(QuickSlLobbyPlayerState));

        if (lobby == null || RunLobbyPlayersAccessor.Value?.GetValue(lobby) is not IEnumerable players)
        {
            return playerStateById;
        }

        foreach (object? player in players)
        {
            if (player == null)
            {
                continue;
            }

            ulong playerId = GetPlayerId(player);
            if (player is not ulong)
            {
                Type playerType = player.GetType();
                object? versionInfo = FindReadableMember(playerType, "versionInfo")?.GetValue(player);
                bool? isModded = FindReadableMember(playerType, "isModded")?.GetValue(player) as bool?;
                playerStateById[playerId] = new QuickSlLobbyPlayerState(versionInfo, isModded);
            }
        }

        return playerStateById;
    }

    internal static void AddConnectedPlayersToLoadLobby(
        LoadRunLobby loadLobby,
        INetGameService netService,
        IEnumerable<ulong> connectedPlayerIds,
        IReadOnlyDictionary<ulong, QuickSlLobbyPlayerState> playerStateById)
    {
        if (netService.Type == NetGameType.Host)
        {
            loadLobby.AddLocalHostPlayer();
        }

        object? players = LoadRunLobbyPlayersAccessor.Value?.GetValue(loadLobby);
        if (players is ICollection<ulong> legacyPlayerIds)
        {
            // 0.107.1–0.109.1：大厅仍直接保存 ulong。
            foreach (ulong playerId in connectedPlayerIds)
            {
                legacyPlayerIds.Add(playerId);
            }

            return;
        }

        if (players is not IList currentPlayers)
        {
            throw new InvalidOperationException("当前游戏版本缺少可写的 LoadRunLobby 玩家集合。");
        }

        Type playerType = GetCollectionElementType(currentPlayers.GetType());
        var existingPlayerIds = new HashSet<ulong>(
            currentPlayers.Cast<object>().Select(GetPlayerId));

        foreach (ulong playerId in connectedPlayerIds)
        {
            if (!existingPlayerIds.Add(playerId))
            {
                continue;
            }

            currentPlayers.Add(CreateLoadRunLobbyPlayer(
                playerType,
                playerId,
                playerStateById.GetValueOrDefault(playerId)));
        }
    }

    internal static void KeepOnlyConnectedRunLobbyPlayers(
        RunManager runManager,
        IReadOnlySet<ulong> connectedPlayerIds)
    {
        if (runManager.RunLobby == null)
        {
            ModLogger.Warn("多人快速 SL：RunLobby 尚未初始化，无法修正已连接玩家列表。");
            return;
        }

        object? players = RunLobbyPlayersAccessor.Value?.GetValue(runManager.RunLobby);
        List<ulong> disconnectedPlayerIds;

        if (players is ICollection<ulong> legacyPlayerIds)
        {
            // 0.107.1–0.109.1：直接从 HashSet<ulong> 移除。
            disconnectedPlayerIds =
            [
                .. legacyPlayerIds.Where(playerId => !connectedPlayerIds.Contains(playerId))
            ];
            foreach (ulong playerId in disconnectedPlayerIds)
            {
                legacyPlayerIds.Remove(playerId);
            }
        }
        else if (players is IList currentPlayers)
        {
            // 0.110：玩家集合改为 List<RunLobbyPlayer>，按对象里的 id 倒序移除。
            disconnectedPlayerIds = [];
            for (int i = currentPlayers.Count - 1; i >= 0; i--)
            {
                ulong playerId = GetPlayerId(currentPlayers[i]);
                if (!connectedPlayerIds.Contains(playerId))
                {
                    disconnectedPlayerIds.Add(playerId);
                    currentPlayers.RemoveAt(i);
                }
            }
        }
        else
        {
            ModLogger.Warn("多人快速 SL：读取 RunLobby 已连接玩家集合失败。");
            return;
        }

        foreach (ulong playerId in disconnectedPlayerIds)
        {
            runManager.InputSynchronizer.OnPlayerDisconnected(playerId);
        }

        if (disconnectedPlayerIds.Count > 0)
        {
            ModLogger.Info($"多人快速 SL：已将 {disconnectedPlayerIds.Count} 个未连接玩家从本次加载同步等待中移除。");
        }
    }

    internal static ulong GetPlayerId(object? player)
    {
        return player switch
        {
            ulong playerId => playerId,
            null => throw new ArgumentNullException(nameof(player)),
            _ => MemberAccessor.Get(player.GetType(), "id").GetValue(player) is ulong playerId
                ? playerId
                : throw new InvalidOperationException($"无法从 {player.GetType().FullName} 读取玩家 ID。")
        };
    }

    private static object CreateLoadRunLobbyPlayer(
        Type playerType,
        ulong playerId,
        QuickSlLobbyPlayerState state)
    {
        object player = TypeAccessor.Get(playerType).CreateInstance()
            ?? throw new InvalidOperationException($"无法创建 {playerType.FullName}。");

        MemberAccessor.Get(playerType, "id").SetValue(player, playerId);
        MemberAccessor.Get(playerType, "isReady").SetValue(player, false);

        MemberAccessor? versionInfoAccessor = FindWritableMember(playerType, "versionInfo");
        if (versionInfoAccessor != null)
        {
            object? versionInfo = state.VersionInfo;
            versionInfo ??= MethodAccessor
                .Get(versionInfoAccessor.ValueType, "LocalDefault", [])
                .InvokeStatic<object>();
            versionInfoAccessor.SetValue(player, versionInfo);
            return player;
        }

        MemberAccessor? isModdedAccessor = FindWritableMember(playerType, "isModded");
        if (isModdedAccessor != null)
        {
            bool isModded = state.IsModded
                ?? throw new InvalidOperationException(
                    $"无法恢复玩家 {playerId} 的 MOD 状态，已中止多人快速 SL。");
            isModdedAccessor.SetValue(player, isModded);
            return player;
        }

        throw new InvalidOperationException(
            $"无法识别 {playerType.FullName} 的版本或 MOD 状态成员。");
    }

    private static Type GetCollectionElementType(Type collectionType)
    {
        Type? collectionInterface = collectionType
            .GetInterfaces()
            .FirstOrDefault(static type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>));
        return collectionInterface?.GetGenericArguments()[0]
            ?? throw new InvalidOperationException($"无法识别 {collectionType.FullName} 的玩家元素类型。");
    }

    private static MemberAccessor? FindReadableMember(Type type, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            try
            {
                MemberAccessor accessor = MemberAccessor.Get(type, memberName);
                if (accessor.CanRead)
                {
                    return accessor;
                }
            }
            catch (MissingMemberException)
            {
                // 当前游戏版本使用另一套大厅成员名称，继续尝试下一个候选。
            }
        }

        return null;
    }

    private static MemberAccessor? FindWritableMember(Type type, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            try
            {
                MemberAccessor accessor = MemberAccessor.Get(type, memberName);
                if (accessor.CanWrite)
                {
                    return accessor;
                }
            }
            catch (MissingMemberException)
            {
                // 当前游戏版本使用另一套大厅成员名称，继续尝试下一个候选。
            }
        }

        return null;
    }
}
