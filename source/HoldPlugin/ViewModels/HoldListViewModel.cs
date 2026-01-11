using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using HoldPlugin.Contracts;

namespace HoldPlugin.ViewModels;

public partial class HoldListViewModel : ObservableObject, IRecipient<RefreshHoldsCommand>
{
    readonly IWindowHandle _windowHandle;
    
    [ObservableProperty] string _holdPointName = "";
    [ObservableProperty] HoldItemViewModel[] _items = [];
    
#if DEBUG
    // Designer ctor
    public HoldListViewModel()
    {
        _windowHandle = null!;
        
        Items =
        [
            new HoldItemViewModel(
                "QFA1",
                38000,
                new ClearedFlightLevel(38000),
                rvsmApproved: true,
                DateTime.MaxValue,
                DateTime.Now.AddMinutes(30),
                "H/RIV",
                HoldItemState.Jurisdiction)
            {
                IsDesignated = true
            },
            new HoldItemViewModel(
                "QFA2",
                28000,
                new ClearedBlockLevel(25000, 28000),
                rvsmApproved: false,
                DateTime.Now.AddMinutes(20),
                DateTime.Now.AddMinutes(30),
                "H/TAR",
                HoldItemState.Handover)
        ];
    }
#endif

    public HoldListViewModel(string pointName, HoldItem[] items, IWindowHandle windowHandle)
    {
        _windowHandle = windowHandle;
        
        HoldPointName = pointName;
        Refresh(items);

        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(RefreshHoldsCommand message)
    {
        Refresh(Plugin.ActiveHolds);
    }

    void Refresh(IEnumerable<HoldItem> holdItems)
    {
        var viewModels = holdItems
            .Where(ShouldDisplay)
            .OrderByDescending(LowestLevel)
            .Select(i => new HoldItemViewModel(i))
            .ToArray();
        
        if (viewModels.Length == 0)
            _windowHandle.Close();

        Items = viewModels;
        return;

        bool ShouldDisplay(HoldItem item)
        {
            if (item.State == HoldItemState.Unconcerned)
                return false;
            
            if (string.IsNullOrEmpty(HoldPointName))
                return !Plugin.HoldLists.Contains(item.HoldPoint);

            return HoldPointName == item.HoldPoint;
        }

        int LowestLevel(HoldItem item)
        {
            return item.ClearedFlightLevel switch
            {
                ClearedFlightLevel clearedFlightLevel => clearedFlightLevel.Level,
                ClearedBlockLevel clearedBlockLevel => clearedBlockLevel.LowerLevel,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}