namespace JcAttractor.Attractor;

public class CallbackInterviewer : IInterviewer
{
    private readonly Func<InterviewQuestion, Task<InterviewAnswer>> _callback;

    public CallbackInterviewer(Func<InterviewQuestion, Task<InterviewAnswer>> callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public CallbackInterviewer(Func<InterviewQuestion, InterviewAnswer> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = q => Task.FromResult(callback(q));
    }

    public Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default)
    {
        return _callback(question);
    }
}
