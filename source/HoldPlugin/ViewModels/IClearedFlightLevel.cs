namespace HoldPlugin.ViewModels;

public interface IClearedFlightLevel;
public record ClearedFlightLevel(int Level) : IClearedFlightLevel;
public record ClearedBlockLevel(int LowerLevel, int UpperLevel) : IClearedFlightLevel;