namespace DotnetCronner;

/// <summary>Emits a progress event to the hook pipeline. Set by the scheduler when a run begins.</summary>
internal delegate Task CronnerProgressEmitter(CronnerHookEvent hookEvent, decimal totalProgress, CronnerProgressInfo? scope);

/// <summary>
/// The default scoped <see cref="ICronnerJobContext"/>. The scheduler resolves it from the execution
/// scope and wires its emitter before the task runs; the task then resolves the same scoped instance and
/// reports progress, which is dispatched to the progress hooks. Fire-and-forget reports are tracked so the
/// scheduler can drain them before firing the terminal hook.
/// </summary>
internal sealed class CronnerJobContext : ICronnerJobContext
{
    private readonly object _gate = new();
    private readonly List<Task> _pending = [];
    private CronnerProgressEmitter? _emitter;
    private int _scopeCounter;

    public decimal TotalProgress { get; private set; }

    /// <summary>Wires the emitter for this run. Called by the scheduler; a task never calls this.</summary>
    internal void Initialize(CronnerProgressEmitter emitter) => _emitter = emitter;

    public void Progress(decimal value)
    {
        TotalProgress = value;
        Track(Emit(CronnerHookEvent.TotalProgressChange, value, null));
    }

    public Task ProgressAsync(decimal value)
    {
        TotalProgress = value;
        return Emit(CronnerHookEvent.TotalProgressChange, value, null);
    }

    public ICronnerProgressScope OpenProgressScope(string? category = null)
    {
        var id = $"scope-{Interlocked.Increment(ref _scopeCounter)}";
        var scope = new CronnerProgressScope(this, id, category);
        Track(Emit(CronnerHookEvent.ProgressScopeOpened, TotalProgress, scope.Snapshot()));
        return scope;
    }

    internal void ReportScope(CronnerProgressScope scope) =>
        Track(Emit(CronnerHookEvent.ScopeProgress, TotalProgress, scope.Snapshot()));

    internal Task ReportScopeAsync(CronnerProgressScope scope) =>
        Emit(CronnerHookEvent.ScopeProgress, TotalProgress, scope.Snapshot());

    internal void CloseScope(CronnerProgressScope scope) =>
        Track(Emit(CronnerHookEvent.ProgressScopeClosed, TotalProgress, scope.Snapshot()));

    internal Task CloseScopeAsync(CronnerProgressScope scope) =>
        Emit(CronnerHookEvent.ProgressScopeClosed, TotalProgress, scope.Snapshot());

    /// <summary>Awaits any fire-and-forget progress hooks started during the run.</summary>
    internal async Task DrainAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _pending.ToArray();
            _pending.Clear();
        }

        if (pending.Length > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private Task Emit(CronnerHookEvent hookEvent, decimal totalProgress, CronnerProgressInfo? scope) =>
        _emitter?.Invoke(hookEvent, totalProgress, scope) ?? Task.CompletedTask;

    private void Track(Task task)
    {
        if (task.IsCompleted)
            return;

        lock (_gate)
            _pending.Add(task);
    }
}

/// <summary>A subtask/category progress scope. Reports through its owning <see cref="CronnerJobContext"/>.</summary>
internal sealed class CronnerProgressScope : ICronnerProgressScope
{
    private readonly CronnerJobContext _owner;
    private int _closed;

    public CronnerProgressScope(CronnerJobContext owner, string id, string? category)
    {
        _owner = owner;
        Id = id;
        Category = category;
    }

    public string Id { get; }

    public string? Category { get; }

    public decimal Value { get; private set; }

    public void Progress(decimal value)
    {
        Value = value;
        _owner.ReportScope(this);
    }

    public Task ProgressAsync(decimal value)
    {
        Value = value;
        return _owner.ReportScopeAsync(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _owner.CloseScope(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            await _owner.CloseScopeAsync(this).ConfigureAwait(false);
    }

    internal CronnerProgressInfo Snapshot() => new() { Id = Id, Category = Category, Value = Value };
}
