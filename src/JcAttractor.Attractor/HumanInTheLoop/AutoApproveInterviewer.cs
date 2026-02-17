namespace JcAttractor.Attractor;

public class AutoApproveInterviewer : IInterviewer
{
    public Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default)
    {
        string selectedText;
        List<string> selectedOptions;

        if (question.Options.Count > 0)
        {
            selectedText = question.Options[0];
            selectedOptions = new List<string> { question.Options[0] };
        }
        else
        {
            // For free text / confirm, use a default affirmative
            selectedText = question.Type == QuestionType.Confirm ? "yes" : "";
            selectedOptions = new List<string>();
        }

        return Task.FromResult(new InterviewAnswer(selectedText, selectedOptions));
    }
}
