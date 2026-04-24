namespace Soulcaster.Attractor.HumanInTheLoop;

public enum QuestionType { SingleSelect, MultiSelect, FreeText, Confirm }
public enum AnswerStatus { Answered, Timeout, Skipped }

public record Choice(string Label, char? AcceleratorKey = null);

public record InterviewQuestion(string Text, QuestionType Type, List<string> Options)
{
    public Dictionary<string, string> Metadata { get; init; } = new();
}
public record InterviewAnswer(string Text, List<string> SelectedOptions, AnswerStatus Status = AnswerStatus.Answered);

public interface IInterviewer
{
    Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default);
}
