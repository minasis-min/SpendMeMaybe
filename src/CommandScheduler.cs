namespace SpendMeMaybe;

public enum ReclickBehavior { Ignore, Cancel, Restart }
public enum CommandRunState { Idle, Sending, Waiting, Holding, Stabilizing, Completed, Cancelled }
public readonly record struct CommandStep(string Command, int WaitSeconds);
public readonly record struct CommandEnvironment(bool LoggedIn, ulong Character, bool CanSend);
public readonly record struct CommandRunStatus(CommandRunState State, string Currency, double RemainingSeconds, string Reason);

// Owned by the framework/UI gate. No tasks, timers or deferred callbacks survive cancellation.
public sealed class CommandScheduler(Func<double> clock)
{
    private sealed class Run(CommandStep[] steps, ulong character, int timeout, double now)
    {
        public readonly CommandStep[] Steps = steps;
        public readonly ulong Character = character;
        public readonly int Timeout = timeout;
        public int Index;
        public double WaitUntil = now, LastSample = now, Held;
        public bool PreviousCanSend = true, NeedsStable;
        public double? SafeSince;
    }
    private Run? current;
    private bool dispatching;
    private CommandRunState state;
    private string name = "", reason = "";
    public bool Active => current != null;
    public bool Request(Guid item, string currencyName, string text, ulong character, ReclickBehavior behavior, int timeout)
    {
        if (!Enum.IsDefined(behavior)) return false;
        if (Active && behavior == ReclickBehavior.Ignore) return false;
        if (Active && behavior == ReclickBehavior.Cancel) { Cancel("アイコンクリック"); return false; }
        // A malformed replacement never destroys a valid running sequence.
        if (character == 0 || !CommandSequence.TryParseSteps(text, out var steps, out _)) return false;
        current = new(steps, character, Math.Clamp(timeout, 5, 120), clock());
        name = currencyName; reason = ""; state = CommandRunState.Sending;
        return true;
    }
    public void Cancel(string why)
    {
        if (current == null) return;
        current = null; reason = why; state = CommandRunState.Cancelled;
    }
    private void Complete() { current = null; reason = ""; state = CommandRunState.Completed; }
    private bool SameCharacter(Run run, CommandEnvironment env)
    {
        if (!env.LoggedIn) { Cancel("ログアウト"); return false; }
        if (env.Character != run.Character) { Cancel("キャラクター変更"); return false; }
        return true;
    }
    public void Tick(Func<CommandEnvironment> environment, Action<string> send)
    {
        if (dispatching || current is not { } run) return;
        dispatching = true;
        try
        {
            var env = environment();
            if (!ReferenceEquals(current, run) || !SameCharacter(run, env)) return;
            var now = clock();
            // Assign elapsed intervals to the previously observed state. Clip out all wait time.
            if (run.Index < run.Steps.Length && !run.PreviousCanSend)
                run.Held += Math.Max(0, now - Math.Max(run.LastSample, run.WaitUntil));
            run.LastSample = now; run.PreviousCanSend = env.CanSend;
            if (!env.CanSend) { run.NeedsStable = true; run.SafeSince = null; }
            else if (run.NeedsStable) run.SafeSince ??= now;
            if (now < run.WaitUntil) { state = CommandRunState.Waiting; return; }
            // The last wait releases the lock even if the game is still loading.
            if (run.Index == run.Steps.Length) { Complete(); return; }
            if (run.Held >= run.Timeout) { Cancel("保留タイムアウト"); return; }
            if (!env.CanSend) { state = CommandRunState.Holding; return; }
            var resuming = run.NeedsStable;
            if (resuming && now - run.SafeSince!.Value < .5) { state = CommandRunState.Stabilizing; return; }
            while (ReferenceEquals(current, run))
            {
                env = environment();
                if (!ReferenceEquals(current, run) || !SameCharacter(run, env)) return;
                if (!env.CanSend)
                {
                    run.PreviousCanSend = false; run.LastSample = clock();
                    run.NeedsStable = true; run.SafeSince = null; state = CommandRunState.Holding; return;
                }
                state = CommandRunState.Sending;
                // Consume before calling another plugin, which may re-enter or cancel us.
                var step = run.Steps[run.Index++];
                send(step.Command);
                if (!ReferenceEquals(current, run)) return;
                now = clock();
                run.WaitUntil = now + step.WaitSeconds;
                run.LastSample = now; run.PreviousCanSend = true;
                run.Held = 0; run.NeedsStable = false; run.SafeSince = null;
                env = environment();
                if (!ReferenceEquals(current, run) || !SameCharacter(run, env)) return;
                run.PreviousCanSend = env.CanSend;
                if (!env.CanSend) { run.NeedsStable = true; run.SafeSince = null; }
                if (step.WaitSeconds > 0) { state = CommandRunState.Waiting; return; }
                if (run.Index == run.Steps.Length) { Complete(); return; }
                if (!env.CanSend) { state = CommandRunState.Holding; return; }
                // A recovery tick sends only one command; ordinary wait-free runs retain their timing.
                if (resuming) return;
            }
        }
        catch { Cancel("実行時エラー"); throw; }
        finally { dispatching = false; }
    }
    public CommandRunStatus Status()
    {
        var remaining = current is not { } run ? 0 : state switch
        {
            CommandRunState.Waiting => Math.Max(0, run.WaitUntil - clock()),
            CommandRunState.Holding => Math.Max(0, run.Timeout - run.Held),
            CommandRunState.Stabilizing => Math.Max(0, .5 - (clock() - (run.SafeSince ?? clock()))),
            _ => 0,
        };
        return new(state, name, remaining, reason);
    }
}
