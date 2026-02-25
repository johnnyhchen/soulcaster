namespace JcAttractor.Attractor;

public enum QuestionType { SingleSelect, MultiSelect, FreeText, Confirm }
public enum AnswerStatus { Answered, Timeout, Skipped }

public record Choice(string Label, char? AcceleratorKey = null);

public record InterviewQuestion(string Text, QuestionType Type, List<string> Options);
public record InterviewAnswer(string Text, List<string> SelectedOptions, AnswerStatus Status = AnswerStatus.Answered);

public interface IInterviewer
{
    Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default);
}
