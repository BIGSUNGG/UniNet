using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 동적 스폰 타입 등록소 — 생성 코드가 NetworkBehaviour 타입마다 기본 팩토리(빈 GameObject + AddComponent)와
    /// 타입 식별자(전체 이름 FNV-1a 64)를 등록한다. 사용자는 RegisterPrefab으로 팩토리를 프리팹 인스턴스 생성으로 교체할 수 있다.
    /// 등록은 메인 스레드(어셈블리 로드·명시 등록), 조회도 메인 스레드(스폰 처리)에서만 일어난다.
    /// </summary>
    public static class UniNetSpawnRegistry
    {
        private static readonly Dictionary<ulong, Func<object>> Factories = new();
        private static readonly Dictionary<Type, ulong> TypeKeys = new();

        /// <summary>타입의 스폰 팩토리를 등록한다 (생성 코드 전용 — typeKey는 컴파일 타임 전체 이름 해시).</summary>
        public static void Register(Type type, ulong typeKey, Func<object> factory)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            Factories[typeKey] = factory;
            TypeKeys[type] = typeKey;
        }

        /// <summary>등록된 타입의 팩토리를 교체한다 — 클라이언트 생성을 프리팹 인스턴스로 바꾼다 (RegisterPrefab).</summary>
        public static void Override(Type type, Func<object> factory)
        {
            if (TypeKeys.TryGetValue(type, out var key)) Factories[key] = factory ?? throw new ArgumentNullException(nameof(factory));
            else throw new InvalidOperationException($"타입 {type.FullName} 은(는) UniNet 스폰 등록이 없습니다 — NetworkBehaviour 파생 partial 타입인지 확인하세요.");
        }

        /// <summary>인스턴스 타입의 스폰 식별자를 조회한다 (0 = 미등록).</summary>
        public static ulong TypeKeyOf(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (TypeKeys.TryGetValue(t, out var key))
                    return key;
            return 0;
        }

        /// <summary>식별자로 인스턴스를 생성한다 (미등록 = null).</summary>
        public static object Create(ulong typeKey)
            => Factories.TryGetValue(typeKey, out var factory) ? factory() : null;
    }
}
