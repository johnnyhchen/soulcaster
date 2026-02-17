namespace JcAttractor.Attractor;

public enum OutcomeStatus { Success, Retry, Fail, PartialSuccess }

public record Outcome(
    OutcomeStatus Status,
    string PreferredLabel = "",
    List<string>? SuggestedNextIds = null,
    Dictionary<string, string>? ContextUpdates = null,
    string Notes = ""
);
