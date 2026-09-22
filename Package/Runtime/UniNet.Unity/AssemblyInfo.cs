using System.Runtime.CompilerServices;

// Test assemblies use runtime internals for observation (RewindSampleCount etc.).
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
[assembly: InternalsVisibleTo("UniNet.Tests.EditMode")]
