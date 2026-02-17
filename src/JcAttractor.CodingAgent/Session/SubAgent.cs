namespace JcAttractor.CodingAgent;

public class SubAgent
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public Session Session { get; }
    public int Depth { get; }

    public SubAgent(Session session, int depth)
    {
        Session = session;
        Depth = depth;
    }
}
