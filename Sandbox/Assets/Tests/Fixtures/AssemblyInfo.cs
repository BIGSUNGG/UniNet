using System.Runtime.CompilerServices;

// Test assemblies reach generated internal members (encoders, dispatch statics) directly to prove end-to-end round-trips.
[assembly: InternalsVisibleTo("UniNet.Tests.EditMode")]
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
