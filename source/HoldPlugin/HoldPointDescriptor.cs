namespace HoldPlugin;

public interface IHoldPointDescriptor;

public record Unallocated : IHoldPointDescriptor;
public record ManuallyAllocated(string HoldPointName) : IHoldPointDescriptor;
public record AutoAllocated(string HoldPointName) : IHoldPointDescriptor;

public static class HoldPointDescriptorExtensions
{
    public static string? GetHoldPointName(this IHoldPointDescriptor descriptor) => descriptor switch
    {
        ManuallyAllocated m => m.HoldPointName,
        AutoAllocated a => a.HoldPointName,
        _ => null
    };

    public static bool Matches(this IHoldPointDescriptor descriptor, string holdPointName) => descriptor switch
    {
        ManuallyAllocated m => m.HoldPointName == holdPointName,
        AutoAllocated a => a.HoldPointName == holdPointName,
        _ => false
    };
}
