using System.Runtime.CompilerServices;

// PlayMode 테스트가 Arena의 internal 관찰자(ServerApplyDamage·IsDead 등)를 사용한다.
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
