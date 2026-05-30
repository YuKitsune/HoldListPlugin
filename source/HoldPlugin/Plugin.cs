using System.ComponentModel.Composition;
using System.Diagnostics;
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

// TODO: Add DLE detection, and Delay menu

// TODO: Inhibit STCA, RAM, DAIW, MSAW, and ETO alerts
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
    IRecipient<HoldPointRemovedCommand>,
    IRecipient<RefreshHoldsCommand>
{
    const string HoldIndicatorStripItemTypePrefix = "HoldPlugin_Indicator_";
    
#if DEBUG
    public const string Name = "Hold Plugin - Debug";
#else
    public const string Name = "Hold Plugin";
#endif

    static readonly Dictionary<string, DateTimeOffset> ErrorMessages = new();

    readonly WorkQueue _workQueue = new(AddError);
    readonly FdrSemaphoreProvider _semaphoreProvider = new();

    private static readonly IHoldPointDescriptor[] _holdLists =
    [
        new Unallocated(),
        new Unallocated(),
        new Unallocated(),
        new Unallocated()
    ];

    static readonly List<HoldItem> _activeHolds = [];
    readonly Dictionary<string, FDP2.FDR> _subscribedFDRs = new();

    public static IReadOnlyCollection<IHoldPointDescriptor> HoldLists => _holdLists;
    public static IReadOnlyCollection<HoldItem> ActiveHolds => _activeHolds.ToArray();

    readonly WindowManager _windowManager;

    string IPlugin.Name => Name;

    public Plugin()
    {
        System.Diagnostics.Debug.WriteLine($"Plugin {Name} created");
        
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
        WeakReferenceMessenger.Default.Register<RefreshHoldsCommand>(this);

        MMI.SelectedTrackChanged += OnSelectedTrackChanged;
        FDP2.FDRsChanged += OnFDRsChanged;

        CreateMenuItems();
    }

    public static void AddError(Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(exception);
        
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
        _windowManager.TryCreateWindow(
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
        foreach (var descriptor in HoldLists)
        {
            var holdPointName = descriptor.GetHoldPointName();

            if (holdPointName is null)
                continue;

            var itemsToDisplay = ActiveHolds
                .Where(h => h.HoldPoint == holdPointName && h.State != HoldItemState.Unconcerned)
                .ToArray();

            if (itemsToDisplay.Any())
            {
                _windowManager.TryCreateWindow(
                    WindowKeys.HoldFor(holdPointName),
                    $"HOLD {holdPointName} WINDOW",
                    handle =>
                    {
                        var viewModel = new HoldListViewModel(holdPointName, itemsToDisplay, handle, new GuiInvoker(MMI.InvokeOnGUI));
                        var view = new HoldList(viewModel);
                        return view;
                    },
                    shrinkToContent: false,
                    canClose: false,
                    canMinimise: false,
                    new Size(480, 150));
            }
        }

        var allocatedNames = GetAllocatedHoldPointNames();
        var otherHolds = ActiveHolds
            .Where(h => !allocatedNames.Contains(h.HoldPoint) && h.State != HoldItemState.Unconcerned)
            .ToArray();
        if (otherHolds.Any())
        {
            _windowManager.TryCreateWindow(
                WindowKeys.HoldOther(),
                "HOLD OTHER WINDOW",
                handle =>
                {
                    var viewModel = new HoldListViewModel(string.Empty, otherHolds, handle, new GuiInvoker(MMI.InvokeOnGUI));
                    var view = new HoldList(viewModel);
                    return view;
                },
                shrinkToContent: false,
                canClose: false,
                canMinimise: false,
                new Size(480, 150));
        }
    }

    static HashSet<string> GetAllocatedHoldPointNames() =>
        _holdLists
            .Select(d => d.GetHoldPointName())
            .Where(n => n is not null)
            .ToHashSet()!;

    void TryAutoAllocateSlot(string holdPoint)
    {
        var alreadyAllocated = _holdLists.Any(d => d.Matches(holdPoint));

        if (alreadyAllocated)
            return;

        var emptySlotIndex = Array.FindIndex(_holdLists, d => d is Unallocated);
        if (emptySlotIndex == -1)
            return;

        _holdLists[emptySlotIndex] = new AutoAllocated(holdPoint);
        WeakReferenceMessenger.Default.Send(new HoldSlotsUpdatedCommand());
    }

    void TryFreeAutoAllocatedSlot(string holdPoint)
    {
        if (_activeHolds.Any(h => h.HoldPoint == holdPoint))
            return;

        var slotIndex = Array.FindIndex(_holdLists, d => d is AutoAllocated && d.Matches(holdPoint));
        if (slotIndex == -1)
            return;

        _holdLists[slotIndex] = new Unallocated();
        _windowManager.CloseWindow(WindowKeys.HoldFor(holdPoint));
        WeakReferenceMessenger.Default.Send(new HoldSlotsUpdatedCommand());
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

            if (!TryFindHoldSegments(flightDataRecord, holdInfo, out var entrySegment, out var exitSegment))
                return null;

            var entryIndex = flightDataRecord.ParsedRoute.IndexOf(entrySegment);
            var exitIndex = flightDataRecord.ParsedRoute.IndexOf(exitSegment);

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

            if (index >= route.Length)
                return null;

            var point = route[index];
            var pointIndex = flightDataRecord.ParsedRoute.IndexOf(point);

            if (pointIndex == entryIndex)
            {
                return new CustomStripItem
                {
                    Text = "A"
                };
            }

            if (pointIndex == exitIndex)
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
        // Hold detection is handled by OnFDRPropertyChanged for immediate response
    }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated)
    {
        _workQueue.Enqueue(async () =>
        {
            try
            {
                if (updated.CoupledFDR is null)
                    return;

                var holdItem = _activeHolds.FirstOrDefault(h => h.Callsign == updated.CoupledFDR.Callsign);
                if (holdItem is not null)
                {
                    holdItem.Level = updated.CorrectedAltitude;
                    WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
                }
            }
            catch (Exception ex)
            {
                AddError(ex);
            }
        });
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

            // Parse exit time if present (3rd segment)
            if (segments.Length >= 3 && int.TryParse(segments[2], out var minutes))
            {
                exitTimeMinutes = minutes;
            }

            // Try matching to a point on the route
            
            var waypoints = fdr.ParsedRoute
                .Skip(fdr.ParsedRoute.OverflownIndex)
                .Where(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT)
                .ToArray();
            
            // Search for exact matches first
            var matchingSegment = waypoints.FirstOrDefault(s => s.Intersection.Name == partialPointName);
            
            // No exact match, check for matches against the first 3 chars
            if (matchingSegment is null && partialPointName.Length >= 3)
            {
                matchingSegment = waypoints.FirstOrDefault(s => s.Intersection.Name.StartsWith(partialPointName));
            }
            
            if (matchingSegment is null)
            {
                continue;
            }

            holdPointName = matchingSegment.Intersection.Name;
            return true;
        }

        return false;
    }

    bool TryFindHoldSegments(
        FDP2.FDR fdr,
        HoldItem holdItem,
        out FDP2.FDR.ExtractedRoute.Segment? holdSegment,
        out FDP2.FDR.ExtractedRoute.Segment? nextSegment)
    {
        holdSegment = null;
        nextSegment = null;

        holdSegment = fdr.ParsedRoute
            .Skip(fdr.ParsedRoute.OverflownIndex)
            .FirstOrDefault(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT && s.Intersection.Name == holdItem.HoldPoint);
        if (holdSegment is null)
            return false;
        
        var holdIndex = fdr.ParsedRoute.IndexOf(holdSegment);
        nextSegment = fdr.ParsedRoute
            .Skip(holdIndex + 1)
            .FirstOrDefault(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT);
        if (nextSegment is null)
            return false;

        return true;
    }

    void OnSelectedTrackChanged(object? sender, EventArgs e)
    {
        _workQueue.Enqueue(async () =>
        {
            try
            {
                var selectedCallsign = MMI.SelectedTrack?.GetFDR()?.Callsign;
                foreach (var hold in _activeHolds.ToArray())
                {
                    hold.IsDesignated = hold.Callsign == selectedCallsign;
                }

                WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
            }
            catch (Exception ex)
            {
                AddError(ex);
            }
        });
    }

    void OnFDRsChanged(object? sender, EventArgs e)
    {
        _workQueue.Enqueue(async () =>
        {
            try
            {
                SubscribeToAllFDRs();
            }
            catch (Exception ex)
            {
                AddError(ex);
            }
        });
    }

    void SubscribeToAllFDRs()
    {
        // Unsubscribe from all previous FDRs (snapshot to avoid enumeration issues)
        foreach (var fdr in _subscribedFDRs.Values.ToArray())
        {
            fdr.PropertyChanged -= OnFDRPropertyChanged;
        }

        _subscribedFDRs.Clear();

        // Subscribe to all current FDRs (snapshot to avoid enumeration issues)
        foreach (var fdr in FDP2.GetFDRs.ToArray())
        {
            _subscribedFDRs[fdr.Callsign] = fdr;
            fdr.PropertyChanged += OnFDRPropertyChanged;
        }

        // Sync hold states in case FDR objects were replaced (e.g. after assuming a tag from NONE,
        // FDRsChanged fires with a new FDR that already has IsTrackedByMe=true, so no
        // PropertyChanged transition fires for that property).
        foreach (var hold in _activeHolds.ToArray())
        {
            if (!_subscribedFDRs.TryGetValue(hold.Callsign, out var fdr))
                continue;

            UpdateHoldItemFromFDR(fdr, hold);
        }

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    /// <summary>
    ///     Initiate, adopt, or cancel holds based on label data and route state.
    ///     Tracks all holds regardless of jurisdiction.
    /// </summary>
    void OnFDRPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not FDP2.FDR fdr)
            return;

        // Only process relevant property changes to avoid queue backlog
        string[] relevantPropertyNames =
        [
            nameof(FDP2.FDR.LabelOpData),
            nameof(FDP2.FDR.GlobalOpData),
            nameof(FDP2.FDR.CFLUpper),
            nameof(FDP2.FDR.CFLLower),
            nameof(FDP2.FDR.RVSM),
            nameof(FDP2.FDR.IsTrackedByMe),
            nameof(FDP2.FDR.IsHandoff),
            nameof(FDP2.FDR.HandoffController),
            nameof(FDP2.FDR.ParsedRoute)
        ];

        if (!relevantPropertyNames.Contains(e.PropertyName))
            return;
        
        Debug.WriteLine($"[HOLD{DateTime.UtcNow:HH:mm:ss}] Queued FDR Update for {fdr.Callsign}");
        
        _workQueue.Enqueue(async () =>
        {
            var semaphore = _semaphoreProvider.Get(fdr.Callsign);
            await semaphore.WaitAsync();

            Debug.WriteLine($"[HOLD {DateTime.UtcNow:HH:mm:ss}] Executing FDR Update for {fdr.Callsign}");

            try
            {
                var existingHold = _activeHolds.FirstOrDefault(h => h.Callsign == fdr.Callsign);
                var hasHoldText = TryParseHoldPointFromLabelOpData(fdr, out var holdPointPrefix, out var exitTimeMinutes);

                if (existingHold != null)
                {
                    if (!hasHoldText)
                    {
                        // H/XXX removed from label data - cancel if we own the FDR
                        if (fdr.IsTrackedByMe)
                        {
                            CancelHold(existingHold);
                        }
                    }
                    else if (exitTimeMinutes.HasValue)
                    {
                        UpdateHold(existingHold, exitTimeMinutes.Value);
                    }

                    // Update FDR-derived properties
                    UpdateHoldItemFromFDR(fdr, existingHold);
                }
                else
                {
                    if (hasHoldText && fdr.IsTrackedByMe)
                    {
                        InitiateHold(fdr, holdPointPrefix, exitTimeMinutes);
                    }
                }

                WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
            }
            catch (Exception ex)
            {
                AddError(ex);
            }
            finally
            {
                semaphore.Release();
            }
        });
    }

    void InitiateHold(FDP2.FDR fdr, string holdPointPrefix, int? exitTimeMinutes)
    {
        var holdEntryPoint = fdr.ParsedRoute
            .Skip(fdr.ParsedRoute.OverflownIndex)
            .FirstOrDefault(s => s.Intersection.Name.StartsWith(holdPointPrefix));
        if (holdEntryPoint is null)
            return;
        
        var holdExitTime = exitTimeMinutes.HasValue
            ? CalculateExitTime(exitTimeMinutes.Value)
            : holdEntryPoint.ETO.Add(TimeSpan.FromMinutes(10));
        
        var holdSegment = fdr.ParsedRoute
            .Skip(fdr.ParsedRoute.OverflownIndex)
            .FirstOrDefault(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT && s.Intersection.Name.StartsWith(holdPointPrefix));

        if (holdSegment is null)
            return;
        
        var holdIndex = fdr.ParsedRoute.IndexOf(holdSegment);
        var nextSegment = fdr.ParsedRoute
            .Skip(holdIndex + 1)
            .FirstOrDefault(s => s.Type == FDP2.FDR.ExtractedRoute.Segment.SegmentTypes.WAYPOINT);

        if (nextSegment is null)
            return;
        
        var holdItem = CreateHoldItem(fdr, holdSegment, nextSegment, holdExitTime);
        UpdatePETOs(fdr, holdItem);
    }

    // void SyncInitiateHold(FDP2.FDR fdr, FDP2.FDR.ExtractedRoute.Segment holdExitSegment)
    // {
    //     try
    //     {
    //         var networkInstanceProperty = typeof(vatsys.Network).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
    //         var networkInstance = (Network)networkInstanceProperty.GetValue(null);
    //
    //         // vatsys.Network.Instance.SendFlightPlanChange(fdr);
    //         // var sendFlightPlanChangeMethod = typeof(vatsys.Network).GetMethod(
    //         //     "SendFlightPlanChange",
    //         //     BindingFlags.NonPublic | BindingFlags.Instance);
    //         // sendFlightPlanChangeMethod.Invoke(networkInstance, [fdr]);
    //
    //     }
    //     catch (TargetInvocationException tie)
    //     {
    //         // Force log even if throttled
    //         var msg = $"Reflection call failed: {tie.InnerException?.GetType().Name} - {tie.InnerException?.Message}";
    //         System.Diagnostics.Debug.WriteLine(msg);
    //     }
    //     catch (Exception ex)
    //     {
    //         AddError(ex);
    //     }
    // }

    void SyncPETO(FDP2.FDR fdr, FDP2.FDR.ExtractedRoute.Segment segment)
    {
        try
        {
            var networkInstanceProperty = typeof(vatsys.Network).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
            var networkInstance = (Network)networkInstanceProperty.GetValue(null);
            
            // vatsys.Network.Instance.SendEST(petoSegment.Parent.Parent, petoSegment, ClientQueryEstimateType.PETO);
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
            sendEstMethod.Invoke(networkInstance, [fdr, segment, ClientQueryEstimateType.PETO, false]);

        }
        catch (TargetInvocationException tie)
        {
            // Force log even if throttled
            var msg = $"Reflection call failed: {tie.InnerException?.GetType().Name} - {tie.InnerException?.Message}";
            System.Diagnostics.Debug.WriteLine(msg);
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    void UpdateHoldItemFromFDR(FDP2.FDR fdr, HoldItem hold)
    {
        IClearedFlightLevel clearedFlightLevel = fdr.CFLLower <= 0
            ? new ClearedFlightLevel(fdr.CFLUpper)
            : new ClearedBlockLevel(fdr.CFLLower, fdr.CFLUpper);

        HoldItemState state;
        if (fdr.IsHandoff || (fdr.IsTrackedByMe && fdr.HandoffController is not null))
            state = HoldItemState.Handover;
        else if (fdr.IsTrackedByMe)
            state = HoldItemState.Jurisdiction;
        else
            state = HoldItemState.Unconcerned;

        hold.ClearedFlightLevel = clearedFlightLevel;
        hold.RvsmApproved = fdr.RVSM;
        hold.GlobalOps = fdr.GlobalOpData;
        hold.State = state;
    }

    DateTime CalculateExitTime(int exitTimeMinutes)
    {
        // Calculate target exit time based on minutes past the hour
        var targetExitTime = new DateTime(
            DateTime.UtcNow.Year,
            DateTime.UtcNow.Month,
            DateTime.UtcNow.Day,
            DateTime.UtcNow.Hour,
            exitTimeMinutes,
            0);

        // If target time is before entry time, it's next hour
        if (targetExitTime <= DateTime.UtcNow)
        {
            targetExitTime = targetExitTime.AddHours(1);
        }

        return targetExitTime;
    }

    HoldItem CreateHoldItem(
        FDP2.FDR fdr,
        FDP2.FDR.ExtractedRoute.Segment holdSegment,
        FDP2.FDR.ExtractedRoute.Segment nextSegment,
        DateTime holdExitTime)
    {
        var selectedCallsign = MMI.SelectedTrack?.GetFDR()?.Callsign;
        var isDesignated = fdr.Callsign == selectedCallsign;

        var level = fdr.CoupledTrack?.CorrectedAltitude ?? fdr.PRL;
        IClearedFlightLevel clearedFlightLevel = fdr.CFLLower <= 0
            ? new ClearedFlightLevel(fdr.CFLUpper)
            : new ClearedBlockLevel(fdr.CFLLower, fdr.CFLUpper);

        HoldItemState state;
        if (fdr.IsHandoff || (fdr.IsTrackedByMe && fdr.HandoffController is not null))
            state = HoldItemState.Handover;
        else if (fdr.IsTrackedByMe)
            state = HoldItemState.Jurisdiction;
        else
            state = HoldItemState.Unconcerned;

        var timeToNextWaypoint = nextSegment.ETO - holdSegment.ETO;
        
        var holdItem = new HoldItem(
            fdr.Callsign,
            holdSegment.Intersection.Name,
            holdSegment.ATO,
            holdExitTime,
            timeToNextWaypoint,
            isDesignated,
            level,
            clearedFlightLevel,
            fdr.RVSM,
            fdr.GlobalOpData,
            state);

        TryAutoAllocateSlot(holdItem.HoldPoint);
        _activeHolds.Add(holdItem);
        EnsureHoldWindowsAreOpen();

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());

        return holdItem;
    }

    void UntrackHold(HoldItem hold)
    {
        _activeHolds.Remove(hold);
        TryFreeAutoAllocatedSlot(hold.HoldPoint);
        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
    }

    void UpdateHold(HoldItem hold, int exitTimeMinutes)
    {
        var exitTime = CalculateExitTime(exitTimeMinutes);
        if (hold.HoldExitTime == exitTime)
            return;
        
        var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == hold.Callsign);
        if (fdr is null)
            return;
        
        hold.HoldExitTime = exitTime;
        
        UpdatePETOs(fdr, hold);

        System.Diagnostics.Debug.WriteLine($"[HOLD DEBUG] Hold updated for {hold.Callsign}, new exit time: {exitTime}");
    }

    void CancelHold(HoldItem hold)
    {
        var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == hold.Callsign);
        if (fdr is null)
        {
            UntrackHold(hold);
            return;
        }

        if (TryFindHoldSegments(fdr, hold, out var entrySegment, out var exitSegment))
        {
            // Clear PETO on both segments to remove the hold
            FDP2.ClearPETO(fdr, entrySegment);
            SyncPETO(fdr, entrySegment);
            
            FDP2.ClearPETO(fdr, exitSegment);
            SyncPETO(fdr, entrySegment);
            
            System.Diagnostics.Debug.WriteLine($"[HOLD DEBUG] Hold cancelled for {hold.Callsign}");
        }

        UntrackHold(hold);
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
        _workQueue.Enqueue(async () =>
        {
            var hold = _activeHolds.FirstOrDefault(h => h.Callsign == message.Callsign);
            if (hold == null)
                return;

            var semaphore = _semaphoreProvider.Get(hold.Callsign);
            await semaphore.WaitAsync();

            try
            {
                CancelHold(hold);
                WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
            }
            finally
            {
                semaphore.Release();
            }
        });
    }

    public void Receive(RemoveHoldItemCommand message)
    {
        _workQueue.Enqueue(async () =>
        {
            var hold = _activeHolds.FirstOrDefault(h => h.Callsign == message.Callsign);
            if (hold == null)
                return;

            var semaphore = _semaphoreProvider.Get(hold.Callsign);
            await semaphore.WaitAsync();

            try
            {
                UntrackHold(hold);
                WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());
            }
            finally
            {
                semaphore.Release();
            }
        });
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
        try
        {
            var holdItem = ActiveHolds.FirstOrDefault(h => h.Callsign == message.Callsign);
            if (holdItem is null)
                return;

            var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == message.Callsign);
            if (fdr is null)
                return;

            if (!TryFindHoldSegments(fdr, holdItem, out var entrySegment, out var exitSegment))
                return;

            // TimeMenu window = new TimeMenu(holdItem.HoldExitTime, TimeSelected: true);
            var window = Activator.CreateInstance(
                Type.GetType("vatsys.TimeMenu, vatsys"),
                [
                    holdItem.HoldExitTime, // Selected time
                    false, // bool TimeSelected
                    true, // bool CancelButton
                    -90, // int TimeCountMin = -90
                    90, //int TimeCountMax = 90
                ]) as BaseForm;

            window.Text = holdItem.Callsign;
            window.StartPosition = FormStartPosition.Manual;
            window.Location = Cursor.Position;
            MMI.EnsureWindowVisible((Form)window);
            window.ShowDialog();

            if (window.DialogResult == DialogResult.OK)
            {
                var selectedTime = (DateTime)window.GetType().GetField("SelectedTime").GetValue(window);
                if (selectedTime >= DateTime.UtcNow)
                {
                    holdItem.HoldExitTime = selectedTime;
                    UpdatePETOs(fdr, holdItem);

                    System.Diagnostics.Debug.WriteLine(
                        $"[HOLD DEBUG] Manual exit time update for {holdItem.Callsign}: {selectedTime}");
                }
            }

            window.Dispose();
        }
        catch (Exception ex)
        {
            AddError(ex);
        }
    }

    void UpdatePETOs(FDP2.FDR fdr, HoldItem holdItem)
    {
        if (!TryFindHoldSegments(fdr, holdItem, out var holdSegment, out var nextSegment))
            return;

        if (holdItem.HoldExitTime > DateTime.UtcNow)
        {
            FDP2.SetPETO(fdr, holdSegment, holdItem.HoldExitTime);
            holdSegment.MPRArmed = false;
            SyncPETO(fdr, holdSegment);
        }
        
        var nextEto = holdItem.HoldExitTime + holdItem.TimeToNextWaypoint;
        if (nextEto > DateTime.UtcNow)
        {
            FDP2.SetPETO(fdr, nextSegment, nextEto);
            nextSegment.MPRArmed = false;
            SyncPETO(fdr, nextSegment);
        }
    }

    public void Receive(ChangeGlobalOpsCommand message)
    {
        _workQueue.Enqueue(async () =>
        {
            var fdr = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == message.Callsign);
            if (fdr is null)
                return;

            var semaphore = _semaphoreProvider.Get(fdr.Callsign);
            await semaphore.WaitAsync();

            try
            {
                fdr.GlobalOpData = message.GlobalOps;
            }
            finally
            {
                semaphore.Release();
            }
        });
    }

    public void Receive(HoldPointAddedCommand message)
    {
        var alreadyAllocated = _holdLists.Any(d => d.Matches(message.PointName));

        if (!alreadyAllocated)
        {
            _holdLists[message.Index] = new ManuallyAllocated(message.PointName);
        }

        WeakReferenceMessenger.Default.Send(new RefreshHoldsCommand());

        // Check if there are any aircraft that should still be in OTHER
        var otherHolds = _activeHolds
            .Where(h => !GetAllocatedHoldPointNames().Contains(h.HoldPoint) && h.State != HoldItemState.Unconcerned)
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
        var index = Array.FindIndex(_holdLists, d => d.Matches(message.PointName));
        if (index == -1)
            return;

        _holdLists[index] = new Unallocated();

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

    public void Receive(RefreshHoldsCommand message)
    {
        EnsureHoldWindowsAreOpen();
    }
}

