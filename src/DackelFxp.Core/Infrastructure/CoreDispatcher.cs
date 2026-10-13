namespace DackelFxp.Core.Infrastructure;

/// <summary>
/// Marshals change notifications to the UI thread. The app sets <see cref="Post"/> at startup;
/// without UI (tests) callbacks run inline.
/// </summary>
public static class CoreDispatcher
{
    public static Action<Action> Post { get; set; } = action => action();
}
