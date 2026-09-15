using System.Collections.Generic;
using System;
using UniNet.Core.Hosting;

namespace UniNet.Core.Hosting
{
    /// <summary>타입별 리플리케이션 핸들 등록소 — 생성 코드가 어셈블리 로드 시 등록한다.</summary>
    public static class UniNetTypeRegistry
    {
        private static readonly Dictionary<Type, UniNetReplicationHandler> Handlers = new();

        /// <summary>타입의 리플리케이션 핸들을 등록한다.</summary>
        public static void Register(Type type, UniNetReplicationHandler handler)
        {
            Handlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>타입의 핸들을 조회한다 — [Replicated]가 중간 기반 클래스에 선언된 경우도 찾도록 체인 탐색.</summary>
        public static UniNetReplicationHandler Find(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (Handlers.TryGetValue(t, out var h))
                    return h;
            return null;
        }
    }
}
