namespace Soulcaster.Attractor.Handlers;

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
        var defaultChoice = node.RawAttributes.GetValueOrDefault("human.default_choice", "");

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

        question = question with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["node_id"] = node.Id,
                ["shape"] = node.Shape,
                ["graph_name"] = graph.Name
            }
        };

        if (!string.IsNullOrWhiteSpace(defaultChoice))
            question.Metadata["default_choice"] = defaultChoice;

        var answer = await _interviewer.AskAsync(question, ct);

        // Handle timeout — check for default choice or return retry
        if (answer.Status == AnswerStatus.Timeout)
        {
            if (!string.IsNullOrWhiteSpace(defaultChoice))
            {
                return new Outcome(
                    Status: OutcomeStatus.Success,
                    PreferredLabel: defaultChoice,
                    Notes: $"Human gate '{node.Id}' timed out, using default: {defaultChoice}"
                );
            }

            return new Outcome(
                Status: OutcomeStatus.Retry,
                Notes: $"Human gate '{node.Id}' timed out with no default choice."
            );
        }

        // Handle skip — return fail
        if (answer.Status == AnswerStatus.Skipped)
        {
            return new Outcome(
                Status: OutcomeStatus.Fail,
                Notes: $"Human gate '{node.Id}' was skipped."
            );
        }

        return new Outcome(
            Status: OutcomeStatus.Success,
            PreferredLabel: answer.Text,
            Notes: $"Human selected: {answer.Text}"
        );
    }
}
