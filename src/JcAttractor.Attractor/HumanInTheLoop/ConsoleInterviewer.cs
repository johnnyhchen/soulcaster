namespace JcAttractor.Attractor;

public class ConsoleInterviewer : IInterviewer
{
    public Task<InterviewAnswer> AskAsync(InterviewQuestion question, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine(question.Text);

        switch (question.Type)
        {
            case QuestionType.SingleSelect:
                return Task.FromResult(HandleSingleSelect(question));

            case QuestionType.MultiSelect:
                return Task.FromResult(HandleMultiSelect(question));

            case QuestionType.FreeText:
                return Task.FromResult(HandleFreeText());

            case QuestionType.Confirm:
                return Task.FromResult(HandleConfirm());

            default:
                return Task.FromResult(HandleFreeText());
        }
    }

    private static InterviewAnswer HandleSingleSelect(InterviewQuestion question)
    {
        for (int i = 0; i < question.Options.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {question.Options[i]}");
        }

        Console.Write("Enter choice (number): ");
        var input = Console.ReadLine()?.Trim() ?? "";

        if (int.TryParse(input, out int index) && index >= 1 && index <= question.Options.Count)
        {
            var selected = question.Options[index - 1];
            return new InterviewAnswer(selected, new List<string> { selected });
        }

        // Try matching by label text
        var match = question.Options.FirstOrDefault(o =>
            o.Equals(input, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return new InterviewAnswer(match, new List<string> { match });

        // Default to first option
        var first = question.Options.Count > 0 ? question.Options[0] : input;
        return new InterviewAnswer(first, new List<string> { first });
    }

    private static InterviewAnswer HandleMultiSelect(InterviewQuestion question)
    {
        for (int i = 0; i < question.Options.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {question.Options[i]}");
        }

        Console.Write("Enter choices (comma-separated numbers): ");
        var input = Console.ReadLine()?.Trim() ?? "";

        var selectedOptions = new List<string>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int index) && index >= 1 && index <= question.Options.Count)
            {
                selectedOptions.Add(question.Options[index - 1]);
            }
        }

        string text = string.Join(", ", selectedOptions);
        return new InterviewAnswer(text, selectedOptions);
    }

    private static InterviewAnswer HandleFreeText()
    {
        Console.Write("Enter response: ");
        var input = Console.ReadLine()?.Trim() ?? "";
        return new InterviewAnswer(input, new List<string>());
    }

    private static InterviewAnswer HandleConfirm()
    {
        Console.Write("Confirm? (y/n): ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        bool confirmed = input is "y" or "yes" or "1" or "true";
        return new InterviewAnswer(confirmed ? "yes" : "no", new List<string> { confirmed ? "yes" : "no" });
    }
}
