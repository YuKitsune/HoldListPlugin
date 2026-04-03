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

public class HoldItem : IDisposable
{
    public FDP2.FDR FDR { get; }
    public string Callsign { get; set; }
    public string HoldPoint { get; set; }
    public bool IsDesignated { get; set; }
    public int Level { get; set; }
    public IClearedFlightLevel ClearedFlightLevel { get; set; }
    public bool RvsmApproved { get; set; }
    public FDP2.FDR.ExtractedRoute.Segment HoldEntryPoint { get; set; }
    public FDP2.FDR.ExtractedRoute.Segment HoldExitPoint { get; set; }
    public string GlobalOps { get; set; }
    public HoldItemState State { get; set; }

    public HoldItem(
        FDP2.FDR fdr,
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
        FDR = fdr;
        Callsign = fdr.Callsign;
        HoldPoint = holdPoint;
        IsDesignated = isDesignated;
        Level = level;
        ClearedFlightLevel = clearedFlightLevel;
        RvsmApproved = rvsmApproved;
        HoldEntryPoint = holdEntryPoint;
        HoldExitPoint = holdExitPoint;
        GlobalOps = globalOps;
        State = state;

        FDR.PropertyChanged += OnFDRPropertyChanged;
        HoldExitPoint.PropertyChanged += OnHoldExitPointPropertyChanged;
    }

    void OnFDRPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            switch (e.PropertyName)
            {
                case nameof(FDR.ParsedRoute):
                    // Scenario 2: Check if entry or exit points no longer exist after overflown index
                    var routeAfterOverflown = FDR.ParsedRoute.Skip(FDR.ParsedRoute.OverflownIndex).ToList();
                    if (!routeAfterOverflown.Contains(HoldEntryPoint) || !routeAfterOverflown.Contains(HoldExitPoint))
                    {
                        WeakReferenceMessenger.Default.Send(new RemoveHoldItemCommand(Callsign));
                    }
                    break;
                case nameof(FDR.CFLUpper):
                case nameof(FDR.CFLLower):
                    ClearedFlightLevel = GetClearedFlightLevel();
                    WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
                    break;
                case nameof(FDR.GlobalOpData):
                    GlobalOps = FDR.GlobalOpData;
                    WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
                    break;
                case nameof(FDR.RVSM):
                    RvsmApproved = FDR.RVSM;
                    WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
                    break;
                case nameof(FDR.ControllerTracking):
                case nameof(FDR.HandoffSector):
                case nameof(FDR.HandoffController):
                    State = GetState();
                    WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
                    break;
                case nameof(FDR.LabelOpData):
                    WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.AddError(ex);
        }
    }

    void OnHoldExitPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            if (e.PropertyName == nameof(HoldExitPoint.ETO))
            {
                var holdDuration = HoldExitPoint.ETO - HoldEntryPoint.ETO;
                HoldExitPoint.EET = holdDuration;
                FDP2.Process(FDR, routeChanged: true);
                WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
            }
        }
        catch (Exception ex)
        {
            Plugin.AddError(ex);
        }
    }

    public void UpdateLevel(int level)
    {
        Level = level;
        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    IClearedFlightLevel GetClearedFlightLevel()
    {
        if (FDR.CFLLower <= 0)
            return new ClearedFlightLevel(FDR.CFLUpper);

        return new ClearedBlockLevel(FDR.CFLLower, FDR.CFLUpper);
    }

    HoldItemState GetState()
    {
        var state = HoldItemState.Unconcerned;
        if (FDR.IsTrackedByMe)
            state = HoldItemState.Jurisdiction;
        if (FDR.IsHandoff || FDR.HandoffController is not null)
            state = HoldItemState.Handover;

        return state;
    }

    public void Dispose()
    {
        FDR.PropertyChanged -= OnFDRPropertyChanged;
        HoldExitPoint.PropertyChanged -= OnHoldExitPointPropertyChanged;
    }
}

public record RefreshHoldsCommand;
public record RemoveHoldItemCommand(string Callsign);

public record HoldPointAddedCommand(int Index, string PointName);
public record HoldPointRemovedCommand(string PointName);
