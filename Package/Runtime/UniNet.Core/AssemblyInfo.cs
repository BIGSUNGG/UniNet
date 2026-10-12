using System.Runtime.CompilerServices;

// Test assemblies — internal test hooks (e.g. the ServerRpc rejection-log throttle reset).
// Mirrors UniNet.Unity's AssemblyInfo (ADR-0026 session: EditMode/PlayMode suites share one editor domain).
[assembly: InternalsVisibleTo("UniNet.Tests.EditMode")]
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
