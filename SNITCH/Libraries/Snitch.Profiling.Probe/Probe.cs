using System;
using System.Diagnostics.Tracing;

namespace Snitch.Profiling.Probe
{
    /// <summary>
    /// The two calls the instrumenter injects into every rewritten method. The whole probe is this
    /// class: it forwards to one <see cref="EventSource"/>, which is what an EventPipe session collects.
    /// When nobody is listening the EventSource call is a cheap enabled check.
    /// </summary>
    public static class Probe
    {
        /// <summary>The EventPipe provider an instrumentation trace is collected from.</summary>
        public const string ProviderName = "Snitch-Profiling-Probe";

        private static readonly ProbeEventSource Source = new ProbeEventSource();

        public static void Enter(int methodId) => Source.Enter(methodId);

        public static void Exit(int methodId) => Source.Exit(methodId);
    }

    /// <summary>
    /// One event per enter and one per exit, carrying only the method id: the names live in the map file
    /// the instrumenter writes next to the rewritten assembly, so the events stay tiny (an instrumented
    /// app emits millions of them).
    /// </summary>
    [EventSource(Name = Probe.ProviderName)]
    internal sealed class ProbeEventSource : EventSource
    {
        [Event(1, Level = EventLevel.Informational)]
        public void Enter(int methodId) => WriteEvent(1, methodId);

        [Event(2, Level = EventLevel.Informational)]
        public void Exit(int methodId) => WriteEvent(2, methodId);
    }
}
