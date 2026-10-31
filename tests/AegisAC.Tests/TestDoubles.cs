using System;
using System.Collections.Generic;
using System.Reflection;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

namespace AegisAC.Tests;

internal sealed class FakePlayerState
{
    public int PlayerID { get; set; }
    public ulong SteamID { get; set; }
    public bool IsValid { get; set; } = true;
    public bool IsFakeClient { get; set; } = false;
    public bool IsAuthorized { get; set; } = true;
    public SyntheticPlayerState Snapshot { get; } = new();
    public Dictionary<string, string> ConVars { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cl_interp_ratio"] = "1.0"
    };
}

public class InterfaceProxy<TInterface> : DispatchProxy where TInterface : class
{
    internal Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            return null;

        if (Handler != null)
            return Handler(targetMethod, args);

        return GetDefaultValue(targetMethod.ReturnType);
    }

    internal static object? GetDefaultValue(Type t)
    {
        if (t == typeof(void))
            return null;
        if (t.IsValueType)
            return Activator.CreateInstance(t);
        return null;
    }
}

internal static class TestPlayerFactory
{
    public static (IPlayer Player, FakePlayerState State) CreatePlayer(
        int id,
        ulong steamId = 76561198000000000UL,
        byte team = 2,
        Vector? origin = null,
        bool isBot = false,
        bool alive = true,
        uint ping = 20u,
        string? name = null)
    {
        Vector orig = origin ?? new Vector(0f, 0f, 0f);
        var state = new FakePlayerState
        {
            PlayerID = id,
            SteamID = steamId + (ulong)id,
            IsValid = true,
            IsFakeClient = isBot
        };
        state.Snapshot.PawnValid = true;
        state.Snapshot.TeamNum = team;
        state.Snapshot.Origin = orig;
        state.Snapshot.EyePosition = new Vector(orig.X, orig.Y, orig.Z + 64f);
        state.Snapshot.Alive = alive;
        state.Snapshot.Ping = ping;
        state.Snapshot.PlayerName = name ?? $"Player_{id}";

        var playerProxy = DispatchProxy.Create<IPlayer, InterfaceProxy<IPlayer>>() as InterfaceProxy<IPlayer>;
        playerProxy!.Handler = (method, args) => method.Name switch
        {
            "get_PlayerID" => state.PlayerID,
            "get_SteamID" => state.SteamID,
            "get_IsValid" => state.IsValid,
            "get_IsFakeClient" => state.IsFakeClient,
            "get_IsAuthorized" => state.IsAuthorized,
            "get_IsAlive" => state.Snapshot.Alive,
            "get_Name" => state.Snapshot.PlayerName,
            "get_PlayerPawn" => null,
            "get_Pawn" => null,
            "get_Controller" => null,
            "get_ServerSideClient" => null,
            "GetClientConvarValue" => args is { Length: > 0 } && args[0] is string cv && state.ConVars.TryGetValue(cv, out var val) ? val : null,
            _ => InterfaceProxy<IPlayer>.GetDefaultValue(method.ReturnType)
        };

        var player = (IPlayer)(object)playerProxy;
        PlayerSnapshotProvider.Register(player, state.Snapshot);
        return (player, state);
    }

    public static void SetPosition(FakePlayerState state, Vector origin, bool crouched = false, bool airborne = false)
    {
        state.Snapshot.Origin = origin;
        float eyeHeight = crouched ? 46f : 64f;
        state.Snapshot.EyePosition = new Vector(origin.X, origin.Y, origin.Z + eyeHeight);
        state.Snapshot.Crouched = crouched;
        state.Snapshot.Airborne = airborne;
    }
}
