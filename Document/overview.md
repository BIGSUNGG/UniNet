# overview — UniNet 프로젝트 개요

## 한 줄 정의

UniNet은 **상용 유니티 게임 서버용 네트워크 프레임워크 라이브러리**다. Unity의 `MonoBehaviour` 객체를 기준으로 RPC 호출과 변수 리플리케이션을 제공하고, 언리얼 엔진 Network Framework의 기능 수준을 목표로 한다.

## 원칙

> **"사용은 간단하게, 기능은 강력하게"**

- 사용자(게임 개발자) API는 최소한의 속성·선언으로 동작해야 한다 — 내부 복잡도를 숨긴다
- 기능은 상용 서버 수준(언리얼 네트워크 프레임워크 패리티)을 목표로 한다 — [[roadmap]] 참조

## 핵심 기능 (목표)

1. **MonoBehaviour 기준 RPC 함수 호출** — 오브젝트 단위의 Server/Client/Multicast RPC
2. **MonoBehaviour 변수 자동 Replicate** — 필드 선언만으로 상태 동기화 (조건·델타 전송 포함)
3. **언리얼 Network Framework 기능 패리티** — relevancy, priority, dormancy, 예측 훅 등 전체 목록과 단계는 [[roadmap]]

## 기술 스택 / 의존성

| 구성 요소 | 역할 | 비고 |
| --- | --- | --- |
| **UniNet** (이 저장소) | Unity/MonoBehaviour 통합 계층, 리플리케이션 엔진 | 이 라이브러리 |
| **DRPC** | RPC 계약·허브·전달 모드 (인터페이스 기반 소스 제너레이션) | 적극 활용 |
| **MessageProtocol** | 이진 메시지 직렬화 (소스 제너레이션, pooled) | 적극 활용 |
| Communication | RUDP 전송 + DTLS 1.2 (DRPC가 위임받아 사용) | 간접 의존 |
| Unity | **Unity 6** 이상 | 서버도 Unity 서버 빌드 |

## 서버 모델

- **Unity 서버 빌드**(dedicated/listen) — UE의 dedicated server처럼 `MonoBehaviour` 객체가 서버에서도 생성·실행된다. 서버 권위(server-authoritative) 모델.
- 클라이언트와 서버가 같은 UniNet API를 쓴다 (네트워크 역할에 따라 동작 분기).

## 현재 상태

- 2026-09-13: 프로젝트 정의·스코프·기반 스택 확정. 기능 구현 미착수. 결정 배경: [[0002-기반-스택과-스코프-확정]]
- 구조 설계: [[architecture]], 기능 로드맵: [[roadmap]], 규약: [[conventions]]
