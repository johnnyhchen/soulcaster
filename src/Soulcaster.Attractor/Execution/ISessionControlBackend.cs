namespace Soulcaster.Attractor.Execution;

public interface ISessionControlBackend
{
    bool ResetThread(string threadId);
}
