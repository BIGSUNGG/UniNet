using System.Runtime.CompilerServices;

// 테스트 어셈블리가 런타임 내부 관찰자(RewindSampleCount 등)를 사용한다.
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
[assembly: InternalsVisibleTo("UniNet.Tests.EditMode")]
