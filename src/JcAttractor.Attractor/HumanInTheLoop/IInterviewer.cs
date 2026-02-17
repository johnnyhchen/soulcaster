namespace JcAttractor.Attractor;

public enum QuestionType { SingleSelect, MultiSelect, FreeText, Confirm }

public record InterviewQuestion(string Text, QuestionType Type, List<string> Options);
public record InterviewAnswer(string Text, List<string> SelectedOptions);

public interface IInterviewer
{
    Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default);
}
