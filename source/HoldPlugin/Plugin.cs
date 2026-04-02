using System.ComponentModel.Composition;
using System.Reflection;
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

// BUG: Infinite loop updating hold item (Updating ETOs and route -> FDR Updated, Update hold -> Update ETO, etc.)
// BUG: Repeated "Collection was modified, enumeration may not execute"
// BUG: Hold exit point isn't synchronised
// BUG: Changing the hold exit time causes the entry time to be off, maybe need a custom time selector?
// BUG: Aircraft remain in hold window after handoff. Need to remove them completely after handoff
// BUG: Hold waypoint sometimes disappears when FDR updates

// TODO: Cancel hold after hold exit time
// TODO: Cancel hold after re-route

// TODO: Replace Strip_Point click action
// TODO: Add DLE detection, and Delay menu

// TODO: Inhibit STCA, RAM, DAIW, MSAW, and ETO alerts
// TODO: Don't remove the hold segments when dropping the tag or handing off
// TODO: Smaller label when in the hold
// TODO: Clean up

[Export(typeof(IPlugin))]
public class Plugin
    : IStripPlugin,
    IRecipient<DesignateAircraftCommand>,
    IRecipient<CancelHoldCommand>,
    IRecipient<OpenClearedLevelMenuCommand>,
    IRecipient<OpenHoldExitMenuCommand>,
    IRecipient<ChangeGlobalOpsCommand>,
    IRecipient<RemoveHoldItemCommand>,
    IRecipient<HoldPointAddedCommand>,
    IRecipient<HoldPointRemovedCommand>
{
    const string HoldIndicatorStripItemTypePrefix = "HoldPlugin_Indicator_";
    
#if DEBUG
    public const string Name = "Hold Plugin - Debug";
#else
    public const string Name = "Hold Plugin";
#endif

    static readonly Dictionary<string, DateTimeOffset> ErrorMessages = new();

    private static readonly string[] _holdLists =
    [
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty
    ];

    static readonly List<HoldItem> _activeHolds = [];
    readonly Dictionary<string, FDP2.FDR> _subscribedFDRs = new();

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
        WeakReferenceMessenger.Default.Register<RemoveHoldItemCommand>(this);
        WeakReferenceMessenger.Default.Register<HoldPointAddedCommand>(this);
        WeakReferenceMessenger.Default.Register<HoldPointRemovedCommand>(this);

        MMI.SelectedTrackChanged += OnSelectedTrackChanged;
        FDP2.FDRsChanged += OnFDRsChanged;

        CreateMenuItems();
    }

    public static void AddError(Exception exception)
    {
        lock (ErrorMessages)
        {
            // Don't flood the error window with the same message over and over again
            if (ErrorMessages.TryGetValue(exception.Message, out var lastShown) &&
                DateTimeOffset.Now - lastShown <= TimeSpan.FromMinutes(1))
                return;

            Errors.Add(exception, Name);
            ErrorMessages[exception.Message] = DateTimeOffset.Now;
        }
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
            },
            canMinimise: false);
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
                    canClose: false,
                    canMinimise: false,
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
                },
                shrinkToContent: false,
                canClose: false,
                canMinimise: false,
                new Size(480, 150));
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
            AddError(ex);
            return null;
        }
    }

    public void OnFDRUpdate(FDP2.FDR updated)
    {
        // Unused
        // Hold detection is handled by OnAnyFDRPropertyChanged for immediate response
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated)
    {
        try
        {
            if (updated.CoupledFDR is null)
                return;

            var holdItem = _activeHolds.FirstOrDefault(h => h.Callsign == updated.CoupledFDR.Callsign);
            holdItem?.UpdateLevel(updated.CorrectedAltitude);
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    bool TryParseHoldPointFromLabelOpData(FDP2.FDR fdr, out string holdPointName, out int? exitTimeMinutes)
    {
        holdPointName = string.Empty;
        exitTimeMinutes = null;

        var parts = fdr.LabelOpData.Split(' ');
        foreach (var part in parts)
        {
            // Valid formats:
            // H/RIVET or H\RIVET - initiate only
            // H/RIVET/42 or H\RIVET\42 - can be adopted
            if (!part.StartsWith("H/") && !part.StartsWith("H\\"))
                continue;

            var segments = part.Split('/', '\\');
            if (segments.Length < 2)
                continue;

            var partialPointName = segments[1];
            if (partialPointName.Length < 3)
                continue;

            // Parse exit time if present (3rd segment)
            if (segments.Length >= 3 && int.TryParse(segments[2], out var minutes))
            {
                exitTimeMinutes = minutes;
            }

            // Try matching to one of the pre-defined hold points
            var fullPointName = _holdLists.FirstOrDefault(h => h.StartsWith(partialPointName));
            if (!string.IsNullOrEmpty(fullPointName))
            {
                holdPointName = fullPointName;
                return true;
            }

            // Try matching to a point on the route instead
            var matchingSegment = fdr.ParsedRoute
                .Skip(fdr.ParsedRoute.OverflownIndex)
                .FirstOrDefault(s =>
                    s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT &&
                    s.Intersection.Name.StartsWith(partialPointName));
            if (matchingSegment is null)
                continue;

            holdPointName = matchingSegment.Intersection.Name;
            return true;
        }

        return false;
    }
    
    void OnSelectedTrackChanged(object? sender, EventArgs e)
    {
        try
        {
            var selectedCallsign = MMI.SelectedTrack?.GetFDR()?.Callsign;
            foreach (var hold in _activeHolds)
            {
                hold.IsDesignated = hold.Callsign == selectedCallsign;
            }

            WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    void OnFDRsChanged(object? sender, EventArgs e)
    {
        try
        {
            SubscribeToAllFDRs();
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    void SubscribeToAllFDRs()
    {
        // Unsubscribe from all previous FDRs
        foreach (var fdr in _subscribedFDRs.Values)
        {
            fdr.PropertyChanged -= OnFDRPropertyChanged;
        }

        _subscribedFDRs.Clear();

        // Subscribe to all current FDRs
        foreach (var fdr in FDP2.GetFDRs)
        {
            _subscribedFDRs[fdr.Callsign] = fdr;
            fdr.PropertyChanged += OnFDRPropertyChanged;
        }
    }

    /// <summary>
    ///     Initiate, adopt, or cancel holds based on label data and route state.
    ///     Tracks all holds regardless of jurisdiction.
    /// </summary>
    void OnFDRPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        try
        {
            if (sender is not FDP2.FDR fdr)
                return;

            var existingHold = _activeHolds.FirstOrDefault(h => h.Callsign == fdr.Callsign);
            var hasHoldText = TryParseHoldPointFromLabelOpData(fdr, out var holdPointPrefix, out var exitTimeMinutes);

            if (existingHold != null)
            {
                if (!hasHoldText)
                {
                    // Hold text removed, cancel the hold
                    CancelHold(existingHold);
                }
                else if (exitTimeMinutes.HasValue)
                {
                    UpdateHold(existingHold, exitTimeMinutes.Value);
                }

                // Note: Route removal detected by HoldItem PropertyChanged
            }
            else
            {
                if (hasHoldText)
                {
                    // Not tracking yet
                    if (fdr.IsTrackedByMe)
                    {
                        // We own it, initiate new hold
                        InitiateHold(fdr, holdPointPrefix, exitTimeMinutes);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    void InitiateHold(FDP2.FDR fdr, string holdPointPrefix, int? exitTimeMinutes)
    {
        var holdEntryPoint = fdr.ParsedRoute
            .Skip(fdr.ParsedRoute.OverflownIndex)
            .FirstOrDefault(s => s.Intersection.Name.StartsWith(holdPointPrefix));
        if (holdEntryPoint is null)
            return;

        var holdDuration = exitTimeMinutes.HasValue
            ? CalculateHoldDuration(holdEntryPoint.ETO, exitTimeMinutes.Value)
            : TimeSpan.FromMinutes(10);

        var holdingExitPoint = CreateHoldExitSegment(holdEntryPoint, holdDuration);
        CreateHoldItem(fdr, holdEntryPoint, holdingExitPoint);
        
        // Insert the exit point into the route
        var index = fdr.ParsedRoute.IndexOf(holdEntryPoint);
        fdr.ParsedRoute.Insert(index + 1, holdingExitPoint);

        // Re-calculate the onward ETOs
        FDP2.Process(fdr, routeChanged: true);
        
        // Sync the route to other clients
        SyncInitiateHold(fdr, holdingExitPoint);
    }

    void SyncInitiateHold(FDP2.FDR fdr, FDP2.FDR.ExtractedRoute.Segment holdExitSegment)
    {
        try
        {
            // vatsys.Network.Instance.SendFlightPlanChange(fdr);
            // vatsys.Network.Instance.SendEST(petoSegment.Parent.Parent, petoSegment, ClientQueryEstimateType.PETO);
            var networkInstanceProperty = typeof(vatsys.Network).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
            var networkInstance = (Network)networkInstanceProperty.GetValue(null);

            var sendFlightPlanChangeMethod = typeof(vatsys.Network).GetMethod("SendFlightPlanChange", BindingFlags.NonPublic | BindingFlags.Instance);
            sendFlightPlanChangeMethod.Invoke(networkInstance, [fdr]);

            var sendEstMethod = typeof(vatsys.Network).GetMethod(
                "SendEST",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [
                    typeof(FDP2.FDR),
                    typeof(FDP2.FDR.ExtractedRoute.Segment),
                    typeof(ClientQueryEstimateType),
                    typeof(bool)
                ],
                null);
            sendEstMethod.Invoke(networkInstance, [holdExitSegment.Parent.Parent, holdExitSegment, ClientQueryEstimateType.PETO, false]);
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    TimeSpan CalculateHoldDuration(DateTime entryEto, int exitTimeMinutes)
    {
        // Calculate target exit time based on minutes past the hour
        var targetExitTime = new DateTime(
            entryEto.Year,
            entryEto.Month,
            entryEto.Day,
            entryEto.Hour,
            exitTimeMinutes,
            0);

        // If target time is before entry time, it's next hour
        if (targetExitTime <= entryEto)
        {
            targetExitTime = targetExitTime.AddHours(1);
        }

        return targetExitTime - entryEto;
    }

    void CreateHoldItem(
        FDP2.FDR fdr,
        FDP2.FDR.ExtractedRoute.Segment entrySegment,
        FDP2.FDR.ExtractedRoute.Segment exitSegment)
    {
        var selectedCallsign = MMI.SelectedTrack?.GetFDR()?.Callsign;
        var isDesignated = fdr.Callsign == selectedCallsign;

        var level = fdr.CoupledTrack?.CorrectedAltitude ?? fdr.PRL;
        IClearedFlightLevel clearedFlightLevel = fdr.CFLLower <= 0
            ? new ClearedFlightLevel(fdr.CFLUpper)
            : new ClearedBlockLevel(fdr.CFLLower, fdr.CFLUpper);

        var state = HoldItemState.Unconcerned;

        if (fdr.IsTrackedByMe)
            state = HoldItemState.Jurisdiction;

        if (fdr.IsHandoff || fdr.HandoffController is not null)
            state = HoldItemState.Handover;

        var holdItem = new HoldItem(
            fdr,
            exitSegment.Intersection.Name,
            isDesignated,
            level,
            clearedFlightLevel,
            fdr.RVSM,
            entrySegment,
            exitSegment,
            fdr.GlobalOpData,
            state);

        _activeHolds.Add(holdItem);
        EnsureHoldWindowsAreOpen();

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    void StopTracking(HoldItem hold)
    {
        hold.Dispose();
        _activeHolds.Remove(hold);
        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    void UpdateHold(HoldItem hold, int exitTimeMinutes)
    {
        var duration = CalculateHoldDuration(hold.HoldEntryPoint.ETO, exitTimeMinutes);
        
        var exitEto = hold.HoldEntryPoint.ETO.Add(duration);
        hold.HoldExitPoint.EET = duration;
        hold.HoldExitPoint.ETO = exitEto;
        FDP2.Process(hold.FDR, routeChanged: true);
    }

    void CancelHold(HoldItem hold)
    {
        var fdr = hold.FDR;

        // Remove H/XXXXX from LabelOpData
        var parts = fdr.LabelOpData.Split(' ');
        var filteredParts = parts.Where(p => !p.StartsWith("H/") && !p.StartsWith("H\\")).ToArray();
        fdr.LabelOpData = string.Join(" ", filteredParts);

        // Exit segment removal handled by HoldItem.Dispose()
        StopTracking(hold);
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
            EET = holdDuration,
            AirwayName = holdEntrySegment.AirwayName,
            SIDSTARName = holdEntrySegment.SIDSTARName,
            Type = FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT,
            PCL = holdEntrySegment.PCL,
            ETO = holdEntrySegment.ETO.Add(holdDuration),
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
        var hold = _activeHolds.FirstOrDefault(h => h.Callsign == message.Callsign);
        if (hold != null)
            CancelHold(hold);

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    public void Receive(RemoveHoldItemCommand message)
    {
        var hold = _activeHolds.FirstOrDefault(h => h.Callsign == message.Callsign);
        if (hold != null)
            CancelHold(hold);

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
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

        // Check if there are any aircraft that should still be in OTHER
        var otherHolds = _activeHolds
            .Where(h => !HoldLists.Contains(h.HoldPoint) && h.State != HoldItemState.Unconcerned)
            .ToArray();

        // Close OTHER window only if no aircraft should be in it
        if (!otherHolds.Any())
        {
            _windowManager.CloseWindow(WindowKeys.HoldOther());
        }

        EnsureHoldWindowsAreOpen();
    }

    public void Receive(HoldPointRemovedCommand message)
    {
        var index = Array.IndexOf(_holdLists, message.PointName);
        if (index == -1)
            return;

        _holdLists[index] = string.Empty;

        // Close the window for this hold point
        _windowManager.CloseWindow(WindowKeys.HoldFor(message.PointName));

        // Check if any aircraft were holding at this point
        var holdsAtRemovedPoint = _activeHolds.Where(h => h.HoldPoint == message.PointName).ToArray();

        // If aircraft exist, they need to be shown in OTHER window
        if (holdsAtRemovedPoint.Any())
        {
            WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
            EnsureHoldWindowsAreOpen();
        }
    }
}

