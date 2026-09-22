using System.Runtime.CompilerServices;

// PlayMode tests use Arena's internal observers (ServerApplyDamage, IsDead, etc.).
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
