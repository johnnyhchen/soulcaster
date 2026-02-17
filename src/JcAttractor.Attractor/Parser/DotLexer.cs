namespace JcAttractor.Attractor;

public enum DotTokenType
{
    Digraph,
    Graph,
    Node,
    Edge,
    Subgraph,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    Arrow,
    Equals,
    Comma,
    Semicolon,
    Identifier,
    QuotedString,
    Number,
    Boolean,
    Eof
}

public record DotToken(DotTokenType Type, string Value, int Line, int Column);

public class DotLexer
{
    private readonly string _input;
    private int _pos;
    private int _line;
    private int _col;

    public DotLexer(string input)
    {
        _input = StripComments(input);
        _pos = 0;
        _line = 1;
        _col = 1;
    }

    private static string StripComments(string input)
    {
        var result = new System.Text.StringBuilder(input.Length);
        int i = 0;
        bool inQuote = false;

        while (i < input.Length)
        {
            if (inQuote)
            {
                if (input[i] == '\\' && i + 1 < input.Length)
                {
                    result.Append(input[i]);
                    result.Append(input[i + 1]);
                    i += 2;
                    continue;
                }
                if (input[i] == '"')
                {
                    inQuote = false;
                }
                result.Append(input[i]);
                i++;
                continue;
            }

            if (input[i] == '"')
            {
                inQuote = true;
                result.Append(input[i]);
                i++;
                continue;
            }

            // Line comment
            if (input[i] == '/' && i + 1 < input.Length && input[i + 1] == '/')
            {
                while (i < input.Length && input[i] != '\n')
                    i++;
                continue;
            }

            // Block comment
            if (input[i] == '/' && i + 1 < input.Length && input[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < input.Length && !(input[i] == '*' && input[i + 1] == '/'))
                    i++;
                if (i + 1 < input.Length)
                    i += 2; // skip */
                continue;
            }

            // # line comment (common in DOT)
            if (input[i] == '#')
            {
                while (i < input.Length && input[i] != '\n')
                    i++;
                continue;
            }

            result.Append(input[i]);
            i++;
        }

        return result.ToString();
    }

    private char Current => _pos < _input.Length ? _input[_pos] : '\0';
    private char Peek(int offset = 1) => (_pos + offset) < _input.Length ? _input[_pos + offset] : '\0';
    private bool AtEnd => _pos >= _input.Length;

    private void Advance(int count = 1)
    {
        for (int i = 0; i < count && _pos < _input.Length; i++)
        {
            if (_input[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }

    private void SkipWhitespace()
    {
        while (!AtEnd && char.IsWhiteSpace(Current))
            Advance();
    }

    public List<DotToken> Tokenize()
    {
        var tokens = new List<DotToken>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Type == DotTokenType.Eof)
                break;
        }
        return tokens;
    }

    public DotToken NextToken()
    {
        SkipWhitespace();

        if (AtEnd)
            return new DotToken(DotTokenType.Eof, "", _line, _col);

        int startLine = _line;
        int startCol = _col;

        // Punctuation
        switch (Current)
        {
            case '{':
                Advance();
                return new DotToken(DotTokenType.LBrace, "{", startLine, startCol);
            case '}':
                Advance();
                return new DotToken(DotTokenType.RBrace, "}", startLine, startCol);
            case '[':
                Advance();
                return new DotToken(DotTokenType.LBracket, "[", startLine, startCol);
            case ']':
                Advance();
                return new DotToken(DotTokenType.RBracket, "]", startLine, startCol);
            case '=':
                Advance();
                return new DotToken(DotTokenType.Equals, "=", startLine, startCol);
            case ',':
                Advance();
                return new DotToken(DotTokenType.Comma, ",", startLine, startCol);
            case ';':
                Advance();
                return new DotToken(DotTokenType.Semicolon, ";", startLine, startCol);
            case '-':
                if (Peek() == '>')
                {
                    Advance(2);
                    return new DotToken(DotTokenType.Arrow, "->", startLine, startCol);
                }
                // Could be negative number
                if (char.IsDigit(Peek()))
                {
                    return ReadNumber();
                }
                break;
        }

        // Quoted string
        if (Current == '"')
        {
            return ReadQuotedString();
        }

        // Number
        if (char.IsDigit(Current))
        {
            return ReadNumber();
        }

        // Identifier or keyword
        if (char.IsLetter(Current) || Current == '_')
        {
            return ReadIdentifierOrKeyword();
        }

        // Unknown character - skip
        char unknown = Current;
        Advance();
        return new DotToken(DotTokenType.Identifier, unknown.ToString(), startLine, startCol);
    }

    private DotToken ReadQuotedString()
    {
        int startLine = _line;
        int startCol = _col;
        Advance(); // skip opening quote

        var sb = new System.Text.StringBuilder();
        while (!AtEnd && Current != '"')
        {
            if (Current == '\\' && !AtEnd)
            {
                Advance();
                switch (Current)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'l': sb.Append('\n'); break; // DOT left-align
                    default: sb.Append('\\'); sb.Append(Current); break;
                }
                Advance();
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (!AtEnd)
            Advance(); // skip closing quote

        return new DotToken(DotTokenType.QuotedString, sb.ToString(), startLine, startCol);
    }

    private DotToken ReadNumber()
    {
        int startLine = _line;
        int startCol = _col;
        var sb = new System.Text.StringBuilder();

        if (Current == '-')
        {
            sb.Append(Current);
            Advance();
        }

        while (!AtEnd && (char.IsDigit(Current) || Current == '.'))
        {
            sb.Append(Current);
            Advance();
        }

        return new DotToken(DotTokenType.Number, sb.ToString(), startLine, startCol);
    }

    private DotToken ReadIdentifierOrKeyword()
    {
        int startLine = _line;
        int startCol = _col;
        var sb = new System.Text.StringBuilder();

        while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_'))
        {
            sb.Append(Current);
            Advance();
        }

        string value = sb.ToString();
        string lower = value.ToLowerInvariant();

        return lower switch
        {
            "digraph" => new DotToken(DotTokenType.Digraph, value, startLine, startCol),
            "graph" => new DotToken(DotTokenType.Graph, value, startLine, startCol),
            "node" => new DotToken(DotTokenType.Node, value, startLine, startCol),
            "edge" => new DotToken(DotTokenType.Edge, value, startLine, startCol),
            "subgraph" => new DotToken(DotTokenType.Subgraph, value, startLine, startCol),
            "true" or "false" => new DotToken(DotTokenType.Boolean, lower, startLine, startCol),
            _ => new DotToken(DotTokenType.Identifier, value, startLine, startCol),
        };
    }
}
