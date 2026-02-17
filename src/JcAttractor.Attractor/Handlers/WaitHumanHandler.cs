namespace JcAttractor.Attractor;

public class WaitHumanHandler : INodeHandler
{
    private readonly IInterviewer _interviewer;

    public WaitHumanHandler(IInterviewer interviewer)
    {
        _interviewer = interviewer ?? throw new ArgumentNullException(nameof(interviewer));
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Get outgoing edge labels as choices
        var outgoingEdges = graph.OutgoingEdges(node.Id);
        var options = outgoingEdges
            .Where(e => !string.IsNullOrWhiteSpace(e.Label))
            .Select(e => e.Label)
            .ToList();

        string questionText = !string.IsNullOrWhiteSpace(node.Label) ? node.Label : $"Choose next step for '{node.Id}':";

        InterviewQuestion question;
        if (options.Count > 0)
        {
            question = new InterviewQuestion(questionText, QuestionType.SingleSelect, options);
        }
        else
        {
            question = new InterviewQuestion(questionText, QuestionType.FreeText, new List<string>());
        }

        var answer = await _interviewer.AskAsync(question, ct);

        return new Outcome(
            Status: OutcomeStatus.Success,
            PreferredLabel: answer.Text,
            Notes: $"Human selected: {answer.Text}"
        );
    }
}
