namespace DwgTranslator.Core.Services;
/// <summary>Immutable async-flow context; concurrent drawings cannot change each other's billing.</summary>
public static class TranslationBillingContext
{
    public sealed record Value(string TaskId, string Mode);
    private static readonly AsyncLocal<Value?> Slot = new();
    public static Value? Current => Slot.Value;
    public static IDisposable Enter(string taskId, string mode)
    {
        var previous = Slot.Value;
        Slot.Value = new(taskId, mode);
        return new Scope(previous);
    }
    private sealed class Scope(Value? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
