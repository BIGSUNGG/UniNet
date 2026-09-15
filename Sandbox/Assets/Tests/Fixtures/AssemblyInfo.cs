using System.Runtime.CompilerServices;

// 테스트 어셈블리가 생성 코드 내부 멤버(인코더·디스패치 정적)에 직접 접근해 왕복을 증명한다.
[assembly: InternalsVisibleTo("UniNet.Tests.EditMode")]
[assembly: InternalsVisibleTo("UniNet.Tests.PlayMode")]
