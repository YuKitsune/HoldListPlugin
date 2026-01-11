using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.Messaging;
using HoldPlugin.Contracts;
using HoldPlugin.Controls;
using HoldPlugin.ViewModels;
using vatsys;
using vatsys.Plugin;
using Track = vatsys.Track;

namespace HoldPlugin;

// BUG: Hold waypoint disappears when FDR updates
// BUG: When CFL changes, it doesn't make it back to the plugin. FDR changed isn't the right event, maybe use the OnPropertyChanged?
// BUG: Green border should only exist when designated, should be off-white otherwise

// TODO: Figure out how to do designation properly
// TODO: Fix lists not appearing automatically
// TODO: Hold cancellation
// TODO: Cancel hold after hold exit time
// TODO: Cancel hold after re-route
// TODO: Fix duplicate hold points appearing in the route

[Export(typeof(IPlugin))]
public class Plugin
    : IStripPlugin,
    IRecipient<DesignateAircraftCommand>,
    IRecipient<CancelHoldCommand>,
    IRecipient<OpenClearedLevelMenuCommand>,
    IRecipient<OpenHoldExitMenuCommand>,
    IRecipient<ChangeGlobalOpsCommand>,
    IRecipient<HoldPointAddedCommand>,
    IRecipient<HoldPointRemovedCommand>
{
    const string HoldIndicatorStripItemTypePrefix = "HoldPlugin_Indicator_";
    
#if DEBUG
    public const string Name = "Hold Plugin - Debug";
#else
    public const string Name = "Hold Plugin";
#endif

    private static readonly string[] _holdLists =
    [
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty
    ];

    static readonly List<HoldItem> _activeHolds = [];

    public static IReadOnlyCollection<string> HoldLists => _holdLists;
    public static IReadOnlyCollection<HoldItem> ActiveHolds => _activeHolds.AsReadOnly();

    readonly WindowManager _windowManager;

    string IPlugin.Name => Name;

    public Plugin()
    {
        var guiInvoker = new GuiInvoker(MMI.InvokeOnGUI);
        _windowManager = new WindowManager(guiInvoker);
        
        WeakReferenceMessenger.Default.Register<DesignateAircraftCommand>(this);
        WeakReferenceMessenger.Default.Register<CancelHoldCommand>(this);
        WeakReferenceMessenger.Default.Register<OpenClearedLevelMenuCommand>(this);
        WeakReferenceMessenger.Default.Register<OpenHoldExitMenuCommand>(this);
        WeakReferenceMessenger.Default.Register<ChangeGlobalOpsCommand>(this);
        WeakReferenceMessenger.Default.Register<HoldPointAddedCommand>(this);
        WeakReferenceMessenger.Default.Register<HoldPointRemovedCommand>(this);

        CreateMenuItems();
    }

    void CreateMenuItems()
    {
        var menuItem = new CustomToolStripMenuItem(
            CustomToolStripMenuItemWindowType.Main,
            CustomToolStripMenuItemCategory.Tools,
            new ToolStripMenuItem("Hold Setup"));
        menuItem.Item.Click += (s, e) => OpenHoldSetup();
        
        MMI.AddCustomMenuItem(menuItem);
    }

    void OpenHoldSetup()
    {
        _windowManager.FocusOrCreateWindow(
            WindowKeys.HoldSetup,
            "Hold Setup",
            _ =>
            {
                var viewModel = new HoldSetupViewModel(_holdLists, new ErrorReporter());
                var view = new HoldSetup(viewModel);
                return view;
            });
    }

    void EnsureHoldWindowsAreOpen()
    {
        foreach (var holdList in HoldLists)
        {
            var itemsToDisplay = ActiveHolds
                .Where(h => h.HoldPoint == holdList && h.State != HoldItemState.Unconcerned)
                .ToArray();

            if (itemsToDisplay.Any())
            {
                _windowManager.FocusOrCreateWindow(
                    WindowKeys.HoldFor(holdList),
                    $"HOLD {holdList} WINDOW",
                    handle =>
                    {
                        var viewModel = new HoldListViewModel(holdList, itemsToDisplay, handle);
                        var view = new HoldList(viewModel);
                        return view;
                    },
                    shrinkToContent: false,
                    new Size(480, 150));
            }
        }
        
        var otherHolds = ActiveHolds
            .Where(h => !HoldLists.Contains(h.HoldPoint) && h.State != HoldItemState.Unconcerned)
            .ToArray();
        if (otherHolds.Any())
        {
            _windowManager.FocusOrCreateWindow(
                WindowKeys.HoldOther(),
                "HOLD OTHER WINDOW",
                handle =>
                {
                    var viewModel = new HoldListViewModel(string.Empty, otherHolds, handle);
                    var view = new HoldList(viewModel);
                    return view;
                });
        }
    }
    
    public CustomStripItem? GetCustomStripItem(string itemType, Track track, FDP2.FDR flightDataRecord, RDP.RadarTrack radarTrack)
    {
        if (!itemType.StartsWith(HoldIndicatorStripItemTypePrefix))
            return null;
        
        var parts = itemType.Split('_');
        var indexStr = parts.Last();
        var index = int.Parse(indexStr);
        return GetHoldIndicatorStripItem(flightDataRecord, index);
    }

    CustomStripItem? GetHoldIndicatorStripItem(FDP2.FDR flightDataRecord, int index)
    {
        try
        {
            var holdInfo = _activeHolds.FirstOrDefault(h => h.Callsign == flightDataRecord.Callsign);
            if (holdInfo is null)
                return null;

            FDP2.FDR.ExtractedRoute.Segment[] route;

            var overflownIndex = flightDataRecord.ParsedRoute.OverflownIndex;
            if (overflownIndex > 0)
            {
                var lastIndex = flightDataRecord.ParsedRoute.GetRange(0, overflownIndex + 1).FindLastIndex(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT);
                route = flightDataRecord.ParsedRoute.Skip(lastIndex > 0 ? lastIndex : 0).Where(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT).ToArray();
            }
            else
            {
                route = flightDataRecord.ParsedRoute.Where(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT).ToArray();
            }

            var point = route[index];

            if (holdInfo.HoldEntryPoint == point)
            {
                return new CustomStripItem
                {
                    Text = "A"
                };
            }

            if (holdInfo.HoldExitPoint == point)
            {
                return new CustomStripItem
                {
                    Text = "D"
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    public void OnFDRUpdate(FDP2.FDR updated)
    {
        if (updated.ParsedRoute.Any(s => s.Intersection.Name == "RIVET"))
        {
            InitiateHold(updated);
        }

        UpdateHold(updated);
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

    void UpdateHold(FDP2.FDR flightDataRecord)
    {
        var holdItem = ActiveHolds.FirstOrDefault(h => h.Callsign == flightDataRecord.Callsign);
        if (holdItem is null)
            return;

        holdItem.IsDesignated = IsDesignated(flightDataRecord);
        holdItem.Level = GetLevel(flightDataRecord);
        holdItem.ClearedFlightLevel = GetClearedFlightLevel(flightDataRecord);
        holdItem.RvsmApproved = flightDataRecord.RVSM;
        holdItem.GlobalOps = flightDataRecord.GlobalOpData;
        holdItem.State = GetState(flightDataRecord);

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    bool IsDesignated(FDP2.FDR flightDataRecord)
    {
        return MMI.SelectedTrack?.GetFDR()?.Callsign == flightDataRecord.Callsign;
    }

    int GetLevel(FDP2.FDR flightDataRecord)
    {
        return flightDataRecord.CoupledTrack?.CorrectedAltitude ?? flightDataRecord.PRL;
    }

    IClearedFlightLevel GetClearedFlightLevel(FDP2.FDR flightDataRecord)
    {
        if (flightDataRecord.CFLLower <= 0)
        {
            return new ClearedFlightLevel(flightDataRecord.CFLUpper);
        }
        
        return new ClearedBlockLevel(flightDataRecord.CFLLower, flightDataRecord.CFLUpper);
    }

    HoldItemState GetState(FDP2.FDR flightDataRecord)
    {
        var state = HoldItemState.Unconcerned;
        if (flightDataRecord.IsTrackedByMe)
            state = HoldItemState.Jurisdiction;
        if (flightDataRecord.IsHandoff)
            state = HoldItemState.Handover;

        return state;
    }
    
    void InitiateHold(FDP2.FDR updated)
    {
        if (!updated.ESTed)
            return;
        
        if (_activeHolds.Any(h => h.Callsign == updated.Callsign))
            return;
        
        var holdingPoint = updated.ParsedRoute.Find(s => s.Intersection.Name == "RIVET");
        var holdingExitPoint = CreateHoldExitSegment(holdingPoint, TimeSpan.FromMinutes(10));
        
        var index = updated.ParsedRoute.IndexOf(holdingPoint);
        
        updated.ParsedRoute.Insert(index + 1, holdingExitPoint);

        var holdItem = new HoldItem(
            updated.Callsign,
            holdingPoint.Intersection.Name,
            IsDesignated(updated),
            GetLevel(updated),
            GetClearedFlightLevel(updated),
            updated.RVSM,
            holdingPoint,
            holdingExitPoint,
            updated.GlobalOpData,
            GetState(updated));

        _activeHolds.Add(holdItem);
        EnsureHoldWindowsAreOpen();

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    FDP2.FDR.ExtractedRoute.Segment CreateHoldExitSegment(FDP2.FDR.ExtractedRoute.Segment holdEntrySegment, TimeSpan holdDuration)
    {
        return new FDP2.FDR.ExtractedRoute.Segment(holdEntrySegment.Parent)
        {
            Intersection = holdEntrySegment.Intersection,
            Distance = 0,
            GroundSpeed = holdEntrySegment.GroundSpeed,
            Track = holdEntrySegment.Track,
            RequestedLevel = holdEntrySegment.RequestedLevel,
            RequestedSpeed = holdEntrySegment.RequestedSpeed,
            EET = holdEntrySegment.EET,
            AirwayName = holdEntrySegment.AirwayName,
            SIDSTARName = holdEntrySegment.SIDSTARName,
            Type = FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT,
            PCL = holdEntrySegment.PCL,
            ETO = holdEntrySegment.ETO.Add(holdDuration),
            // SETO = holdEntrySegment.SETO.Add(holdDuration),
            ATO = holdEntrySegment.ATO,
            IsPETO = true
        };
    }

    public void Receive(DesignateAircraftCommand message)
    {
        var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == message.Callsign);
        if (fdr is null)
            return;
        
        var track = MMI.FindTrack(fdr);
        if (track is null)
            return;

        MMI.SelectOrDeselectTrack(track);
    }

    public void Receive(CancelHoldCommand message)
    {
        throw new NotImplementedException();
    }

    public void Receive(OpenClearedLevelMenuCommand message)
    {
        var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == message.Callsign);
        if (fdr is null)
            return;
        
        var track = MMI.FindTrack(fdr);
        if (track is null)
            return;
        
        MMI.OpenCFLMenu(track, Control.MousePosition);
    }

    public void Receive(OpenHoldExitMenuCommand message)
    {
        var holdItem = ActiveHolds.FirstOrDefault(h => h.Callsign == message.Callsign);
        if (holdItem == null)
            return;
        
        MMI.OpenPETOMenu(holdItem.HoldExitPoint);
    }

    public void Receive(ChangeGlobalOpsCommand message)
    {
        var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == message.Callsign);
        if (fdr is null)
            return;

        fdr.GlobalOpData = message.GlobalOps;
    }

    public void Receive(HoldPointAddedCommand message)
    {
        if (!_holdLists.Contains(message.PointName))
        {
            _holdLists[message.Index] = message.PointName;
        }

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
        EnsureHoldWindowsAreOpen();
    }

    public void Receive(HoldPointRemovedCommand message)
    {
        var index = Array.IndexOf(_holdLists, message.PointName);
        if (index == -1)
            return;

        _holdLists[index] = string.Empty;
        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }
}

