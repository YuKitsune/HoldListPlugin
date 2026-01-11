using HoldPlugin.ViewModels;
using vatsys;

namespace HoldPlugin.Contracts;

public record DesignateAircraftCommand(string Callsign);

public record CancelHoldCommand(string Callsign);
public record OpenClearedLevelMenuCommand(string Callsign);
public record OpenHoldExitMenuCommand(string Callsign);
public record ChangeGlobalOpsCommand(string Callsign, string GlobalOps);

public class HoldItem(
    string callsign,
    string holdPoint,
    bool isDesignated,
    int level,
    IClearedFlightLevel clearedFlightLevel,
    bool rvsmApproved,
    FDP2.FDR.ExtractedRoute.Segment holdEntryPoint,
    FDP2.FDR.ExtractedRoute.Segment holdExitPoint,
    string globalOps,
    HoldItemState state)
{
    public string Callsign { get; set; } = callsign;
    public string HoldPoint { get; set; } = holdPoint;
    public bool IsDesignated { get; set; } = isDesignated;
    public int Level { get; set; } = level;
    public IClearedFlightLevel ClearedFlightLevel { get; set; } = clearedFlightLevel;
    public bool RvsmApproved { get; set; } = rvsmApproved;
    public FDP2.FDR.ExtractedRoute.Segment HoldEntryPoint { get; set; } = holdEntryPoint;
    public FDP2.FDR.ExtractedRoute.Segment HoldExitPoint { get; set; } = holdExitPoint;
    public string GlobalOps { get; set; } = globalOps;
    public HoldItemState State { get; set; } = state;
}

public record RefreshHoldsCommand;

public record HoldPointAddedCommand(int Index, string PointName);
public record HoldPointRemovedCommand(string PointName);
