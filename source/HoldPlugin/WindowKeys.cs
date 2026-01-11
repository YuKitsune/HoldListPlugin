namespace HoldPlugin;

public static class WindowKeys
{
    public static string HoldSetup => "hold-setup";
    public static string HoldFor(string pointName) => $"hold-{pointName}";
    public static string HoldOther() => $"hold-other";
}
