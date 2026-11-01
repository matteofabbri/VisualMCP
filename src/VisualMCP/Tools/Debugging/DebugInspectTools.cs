using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Debugging;

namespace VisualMCP.Tools.Debugging;

[McpServerToolType]
public static class DebugInspectTools
{
    [McpServerTool(Name = "debug_threads"), Description(
        "The threads of the debuggee, with the stopped one marked. Use it to pick a thread for debug_stack when " +
        "the process is stopped or to see what the program is doing while running. Read-only.")]
    public static Task<object> DebugThreads(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId)
        => debugger.ThreadsAsync(sessionId);

    [McpServerTool(Name = "debug_stack"), Description(
        "The call stack of a thread, innermost frame first, each frame resolved to file:line when the PDB is " +
        "available. Frame ids feed debug_variables (frameId) and debug_evaluate. This is the call-stack window. " +
        "Read-only.")]
    public static Task<object> DebugStack(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Thread id (default: the stopped thread).")] int? threadId = null,
        [Description("First frame to return (default 0).")] int startFrame = 0,
        [Description("How many frames (default 30, max 200).")] int levels = 30)
        => debugger.StackTraceAsync(sessionId, threadId, startFrame, levels);

    [McpServerTool(Name = "debug_variables"), Description(
        "The variables of a stopped frame: scope Locals (default), Arguments or Autos, each {name, value, type, " +
        "expandable} and, with depth>1, the members of one level of objects. locals/autos/watch in one tool. The " +
        "process must be stopped; on a running one the answer says so.")]
    public static Task<object> DebugVariables(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("Frame id from debug_stack (default: the top frame of the stopped thread).")] int? frameId = null,
        [Description("Scope name: Locals (default), Arguments, Autos (any scope the adapter reports).")] string? scope = null,
        [Description("Maximum variables to return (default 50, max 500).")] int maxVariables = 50,
        [Description("Object expansion depth: 1 = the variables themselves (default), 2-3 = expand members too.")] int depth = 1)
        => debugger.VariablesAsync(sessionId, frameId, scope, maxVariables, depth);

    [McpServerTool(Name = "debug_evaluate"), Description(
        "Evaluate a watch expression in the stopped frame — a variable, a property chain, a method call the " +
        "adapter can run, e.g. 'order.Total' or 'items.Count'. Returns the value, its static type and, for an " +
        "object, its members. This is the watch/data-tip window. Pass newValue to WRITE into the running process " +
        "instead (the Set Value action): the expression is assigned the value and the answer shows what it holds " +
        "afterwards, so a breakpoint plus newValue changes state and debug_continue resumes with it. The process " +
        "must be stopped.")]
    public static Task<object> DebugEvaluate(
        IDebuggerService debugger,
        [Description("Session id.")] string sessionId,
        [Description("The C# expression to evaluate.")] string expression,
        [Description("Frame id from debug_stack (default: the top frame of the stopped thread).")] int? frameId = null,
        [Description("Evaluation context: watch (default), repl, hover.")] string? context = null,
        [Description("Optional: a C# value expression (e.g. \"42\", \"\\\"text\\\"\", \"null\") assigned to the " +
                     "expression before it is read back. This is the debugger's Set Value.")] string? newValue = null)
        => debugger.EvaluateAsync(sessionId, expression, frameId, context, newValue);
}
