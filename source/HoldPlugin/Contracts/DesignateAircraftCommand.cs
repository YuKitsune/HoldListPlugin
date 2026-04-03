using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using HoldPlugin.ViewModels;
using vatsys;

namespace HoldPlugin.Contracts;

public record DesignateAircraftCommand(string Callsign);
public record CancelHoldCommand(string Callsign);
public record OpenClearedLevelMenuCommand(string Callsign);
public record OpenHoldExitMenuCommand(string Callsign);
public record ChangeGlobalOpsCommand(string Callsign, string GlobalOps);

public class HoldItem
{
    public string Callsign { get; set; }
    public string HoldPoint { get; set; }
    public DateTime HoldEntryTime { get; set; }
    public DateTime HoldExitTime { get; set; }
    public bool IsDesignated { get; set; }
    public int Level { get; set; }
    public IClearedFlightLevel ClearedFlightLevel { get; set; }
    public bool RvsmApproved { get; set; }
    public string GlobalOps { get; set; }
    public HoldItemState State { get; set; }

    public HoldItem(
        string callsign,
        string holdPoint,
        DateTime holdEntryTime,
        DateTime holdExitTime,
        bool isDesignated,
        int level,
        IClearedFlightLevel clearedFlightLevel,
        bool rvsmApproved,
        string globalOps,
        HoldItemState state)
    {
        Callsign = callsign;
        HoldPoint = holdPoint;
        HoldEntryTime = holdEntryTime;
        HoldExitTime = holdExitTime;
        IsDesignated = isDesignated;
        Level = level;
        ClearedFlightLevel = clearedFlightLevel;
        RvsmApproved = rvsmApproved;
        GlobalOps = globalOps;
        State = state;
    }
}

public record RefreshHoldsCommand;
public record RemoveHoldItemCommand(string Callsign);

public record HoldPointAddedCommand(int Index, string PointName);
public record HoldPointRemovedCommand(string PointName);
