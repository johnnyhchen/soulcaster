namespace JcAttractor.Attractor;

public class DotParser
{
    private readonly List<DotToken> _tokens;
    private int _pos;
    private Dictionary<string, string> _nodeDefaults = new();
    private Dictionary<string, string> _edgeDefaults = new();

    public DotParser(List<DotToken> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    public static Graph Parse(string dotSource)
    {
        var lexer = new DotLexer(dotSource);
        var tokens = lexer.Tokenize();
        var parser = new DotParser(tokens);
        return parser.ParseGraph();
    }

    private DotToken Current => _pos < _tokens.Count ? _tokens[_pos] : new DotToken(DotTokenType.Eof, "", 0, 0);
    private DotToken Peek(int offset = 1) => (_pos + offset) < _tokens.Count ? _tokens[_pos + offset] : new DotToken(DotTokenType.Eof, "", 0, 0);

    private DotToken Consume(DotTokenType expected)
    {
        if (Current.Type != expected)
            throw new InvalidOperationException($"Expected {expected} but got {Current.Type} ('{Current.Value}') at line {Current.Line}:{Current.Column}");
        var token = Current;
        _pos++;
        return token;
    }

    private DotToken ConsumeAny()
    {
        var token = Current;
        _pos++;
        return token;
    }

    private bool Match(DotTokenType type)
    {
        if (Current.Type == type)
        {
            _pos++;
            return true;
        }
        return false;
    }

    private bool Check(DotTokenType type) => Current.Type == type;

    private string ConsumeValue()
    {
        return Current.Type switch
        {
            DotTokenType.QuotedString => Consume(DotTokenType.QuotedString).Value,
            DotTokenType.Number => Consume(DotTokenType.Number).Value,
            DotTokenType.Boolean => Consume(DotTokenType.Boolean).Value,
            DotTokenType.Identifier => Consume(DotTokenType.Identifier).Value,
            _ => throw new InvalidOperationException($"Expected a value but got {Current.Type} ('{Current.Value}') at line {Current.Line}:{Current.Column}")
        };
    }

    private string ConsumeIdentifier()
    {
        // Accept Identifier, QuotedString, or Number as identifiers (DOT is flexible)
        return Current.Type switch
        {
            DotTokenType.Identifier => Consume(DotTokenType.Identifier).Value,
            DotTokenType.QuotedString => Consume(DotTokenType.QuotedString).Value,
            DotTokenType.Number => Consume(DotTokenType.Number).Value,
            _ => throw new InvalidOperationException($"Expected identifier but got {Current.Type} ('{Current.Value}') at line {Current.Line}:{Current.Column}")
        };
    }

    public Graph ParseGraph()
    {
        var graph = new Graph();

        Consume(DotTokenType.Digraph);

        // Optional graph name
        if (Check(DotTokenType.Identifier) || Check(DotTokenType.QuotedString))
        {
            graph.Name = ConsumeIdentifier();
        }

        Consume(DotTokenType.LBrace);

        while (!Check(DotTokenType.RBrace) && !Check(DotTokenType.Eof))
        {
            ParseStatement(graph);
        }

        Consume(DotTokenType.RBrace);

        return graph;
    }

    private void ParseStatement(Graph graph)
    {
        // Skip stray semicolons
        if (Check(DotTokenType.Semicolon))
        {
            _pos++;
            return;
        }

        // Node defaults: node [attrs]
        if (Check(DotTokenType.Node) && Peek().Type == DotTokenType.LBracket)
        {
            _pos++; // consume 'node'
            var attrs = ParseAttrBlock();
            foreach (var (k, v) in attrs)
                _nodeDefaults[k] = v;
            Match(DotTokenType.Semicolon);
            return;
        }

        // Edge defaults: edge [attrs]
        if (Check(DotTokenType.Edge) && Peek().Type == DotTokenType.LBracket)
        {
            _pos++; // consume 'edge'
            var attrs = ParseAttrBlock();
            foreach (var (k, v) in attrs)
                _edgeDefaults[k] = v;
            Match(DotTokenType.Semicolon);
            return;
        }

        // Subgraph
        if (Check(DotTokenType.Subgraph))
        {
            ParseSubgraph(graph);
            Match(DotTokenType.Semicolon);
            return;
        }

        // Graph-level attribute: graph [attrs] or key = value
        if (Check(DotTokenType.Graph) && Peek().Type == DotTokenType.LBracket)
        {
            _pos++; // consume 'graph'
            var attrs = ParseAttrBlock();
            ApplyGraphAttributes(graph, attrs);
            Match(DotTokenType.Semicolon);
            return;
        }

        // key = value (graph attribute)
        if ((Check(DotTokenType.Identifier) || Check(DotTokenType.QuotedString)) && Peek().Type == DotTokenType.Equals)
        {
            // Check that this is not a node followed by -> (edge) or [ (node attrs)
            // Peek(2) should not be -> or Identifier followed by ->
            string key = ConsumeIdentifier();
            Consume(DotTokenType.Equals);
            string value = ConsumeValue();
            ApplyGraphAttribute(graph, key, value);
            Match(DotTokenType.Semicolon);
            return;
        }

        // Node or edge statement
        if (Check(DotTokenType.Identifier) || Check(DotTokenType.QuotedString) || Check(DotTokenType.Number))
        {
            string firstId = ConsumeIdentifier();

            // Edge statement: id -> id -> ...
            if (Check(DotTokenType.Arrow))
            {
                ParseEdgeStmt(graph, firstId);
                Match(DotTokenType.Semicolon);
                return;
            }

            // Node statement: id [attrs]?
            ParseNodeStmt(graph, firstId);
            Match(DotTokenType.Semicolon);
            return;
        }

        // Unknown statement, skip token
        _pos++;
    }

    private void ParseNodeStmt(Graph graph, string nodeId)
    {
        var attrs = new Dictionary<string, string>(_nodeDefaults);

        if (Check(DotTokenType.LBracket))
        {
            var parsed = ParseAttrBlock();
            foreach (var (k, v) in parsed)
                attrs[k] = v;
        }

        var node = BuildGraphNode(nodeId, attrs);
        graph.Nodes[nodeId] = node;
    }

    private void ParseEdgeStmt(Graph graph, string firstId)
    {
        var chain = new List<string> { firstId };

        while (Check(DotTokenType.Arrow))
        {
            Consume(DotTokenType.Arrow);
            string nextId = ConsumeIdentifier();
            chain.Add(nextId);
        }

        var attrs = new Dictionary<string, string>(_edgeDefaults);

        if (Check(DotTokenType.LBracket))
        {
            var parsed = ParseAttrBlock();
            foreach (var (k, v) in parsed)
                attrs[k] = v;
        }

        // Ensure all nodes in the chain exist
        foreach (var nodeId in chain)
        {
            if (!graph.Nodes.ContainsKey(nodeId))
            {
                var defaultAttrs = new Dictionary<string, string>(_nodeDefaults);
                graph.Nodes[nodeId] = BuildGraphNode(nodeId, defaultAttrs);
            }
        }

        // Create edges for each consecutive pair
        for (int i = 0; i < chain.Count - 1; i++)
        {
            var edge = BuildGraphEdge(chain[i], chain[i + 1], attrs);
            graph.Edges.Add(edge);
        }
    }

    private Dictionary<string, string> ParseAttrBlock()
    {
        var attrs = new Dictionary<string, string>();
        Consume(DotTokenType.LBracket);

        while (!Check(DotTokenType.RBracket) && !Check(DotTokenType.Eof))
        {
            string key = ConsumeIdentifier();
            Consume(DotTokenType.Equals);
            string value = ConsumeValue();
            attrs[key] = value;

            // Optional comma or semicolon separator
            if (Check(DotTokenType.Comma))
                _pos++;
            else if (Check(DotTokenType.Semicolon))
                _pos++;
        }

        Consume(DotTokenType.RBracket);
        return attrs;
    }

    private void ParseSubgraph(Graph graph)
    {
        Consume(DotTokenType.Subgraph);

        // Optional subgraph name
        if (Check(DotTokenType.Identifier) || Check(DotTokenType.QuotedString))
        {
            ConsumeIdentifier(); // consume name, not used
        }

        Consume(DotTokenType.LBrace);

        // Save and restore node defaults for subgraph scope
        var savedNodeDefaults = new Dictionary<string, string>(_nodeDefaults);
        var savedEdgeDefaults = new Dictionary<string, string>(_edgeDefaults);

        while (!Check(DotTokenType.RBrace) && !Check(DotTokenType.Eof))
        {
            ParseStatement(graph);
        }

        Consume(DotTokenType.RBrace);

        _nodeDefaults = savedNodeDefaults;
        _edgeDefaults = savedEdgeDefaults;
    }

    private void ApplyGraphAttributes(Graph graph, Dictionary<string, string> attrs)
    {
        foreach (var (k, v) in attrs)
        {
            ApplyGraphAttribute(graph, k, v);
        }
    }

    private void ApplyGraphAttribute(Graph graph, string key, string value)
    {
        graph.Attributes[key] = value;

        switch (key.ToLowerInvariant())
        {
            case "goal":
                graph.Goal = value;
                break;
            case "label":
                graph.Label = value;
                break;
            case "model_stylesheet":
                graph.ModelStylesheet = value;
                break;
            case "default_max_retry":
                if (int.TryParse(value, out int maxRetry))
                    graph.DefaultMaxRetry = maxRetry;
                break;
            case "retry_target":
                graph.RetryTarget = value;
                break;
            case "fallback_retry_target":
                graph.FallbackRetryTarget = value;
                break;
            case "default_fidelity":
                graph.DefaultFidelity = value;
                break;
        }
    }

    private static GraphNode BuildGraphNode(string id, Dictionary<string, string> attrs)
    {
        return new GraphNode
        {
            Id = id,
            Label = attrs.GetValueOrDefault("label", id),
            Shape = attrs.GetValueOrDefault("shape", "box"),
            Type = attrs.GetValueOrDefault("type", ""),
            Prompt = attrs.GetValueOrDefault("prompt", ""),
            MaxRetries = int.TryParse(attrs.GetValueOrDefault("max_retries", "0"), out var mr) ? mr : 0,
            GoalGate = attrs.GetValueOrDefault("goal_gate", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            RetryTarget = attrs.GetValueOrDefault("retry_target", ""),
            FallbackRetryTarget = attrs.GetValueOrDefault("fallback_retry_target", ""),
            Fidelity = attrs.GetValueOrDefault("fidelity", ""),
            ThreadId = attrs.GetValueOrDefault("thread_id", ""),
            Class = attrs.GetValueOrDefault("class", ""),
            Timeout = attrs.TryGetValue("timeout", out var timeout) ? timeout : null,
            LlmModel = attrs.GetValueOrDefault("model", ""),
            LlmProvider = attrs.GetValueOrDefault("provider", ""),
            ReasoningEffort = attrs.GetValueOrDefault("reasoning_effort", "high"),
            AutoStatus = attrs.GetValueOrDefault("auto_status", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            AllowPartial = attrs.GetValueOrDefault("allow_partial", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            RawAttributes = new Dictionary<string, string>(attrs)
        };
    }

    private static GraphEdge BuildGraphEdge(string from, string to, Dictionary<string, string> attrs)
    {
        return new GraphEdge
        {
            FromNode = from,
            ToNode = to,
            Label = attrs.GetValueOrDefault("label", ""),
            Condition = attrs.GetValueOrDefault("condition", ""),
            Weight = int.TryParse(attrs.GetValueOrDefault("weight", "0"), out var w) ? w : 0,
            Fidelity = attrs.GetValueOrDefault("fidelity", ""),
            ThreadId = attrs.GetValueOrDefault("thread_id", ""),
            LoopRestart = attrs.GetValueOrDefault("loop_restart", "false").Equals("true", StringComparison.OrdinalIgnoreCase)
        };
    }
}
