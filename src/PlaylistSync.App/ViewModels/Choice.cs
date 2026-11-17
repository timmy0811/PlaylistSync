namespace PlaylistSync.App.ViewModels;

/// <summary>A value with its display text, for combo boxes. Value equality, so a fresh instance selects the right item.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
