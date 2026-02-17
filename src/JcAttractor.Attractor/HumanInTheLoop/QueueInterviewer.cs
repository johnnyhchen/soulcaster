namespace JcAttractor.Attractor;

public class QueueInterviewer : IInterviewer
{
    private readonly Queue<InterviewAnswer> _answers;

    public QueueInterviewer(IEnumerable<InterviewAnswer> answers)
    {
        _answers = new Queue<InterviewAnswer>(answers);
    }

    public QueueInterviewer(params InterviewAnswer[] answers)
    {
        _answers = new Queue<InterviewAnswer>(answers);
    }

    public void Enqueue(InterviewAnswer answer) => _answers.Enqueue(answer);

    public int Remaining => _answers.Count;

    public Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default)
    {
        if (_answers.Count == 0)
        {
            throw new InvalidOperationException(
                $"QueueInterviewer has no more pre-filled answers. Question was: '{question.Text}'");
        }

        var answer = _answers.Dequeue();
        return Task.FromResult(answer);
    }
}
