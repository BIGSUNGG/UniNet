using System;

namespace UniNet.Core
{
    /// <summary>서버 권위 변수 리플리케이션 대상 필드. 변경 시 연결된 클라이언트에 동기화된다.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ReplicatedAttribute : Attribute
    {
        /// <summary>RepNotify 콜백 메서드 이름 (nameof로 지정). 클라에서 값이 네트워크로 변경될 때 이전값 1개를 인자로 호출된다.</summary>
        public string Notify { get; set; }
    }
}
