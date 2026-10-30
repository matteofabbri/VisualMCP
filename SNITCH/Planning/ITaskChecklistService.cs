namespace Snitch.Planning;

public interface ITaskChecklistService
{
    object Create(string title, string[] steps, string? fileName, string? directory);
    object Update(string path, int[]? done, int[]? notDone);
}
