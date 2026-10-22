# ADR-0054 — NetworkTransform 지속 회전·스케일 동기화 (옵트인 확장, TRS-001)

- 상태: 채택 (2026-10-22)
- 관련: ADR-0013(위치 전용 — "필요 시 확장" 트리거 충족), ADR-0037(델타·notify), ADR-0038(예측), TRS-001(감사 항목)
- 버전: 0.21.0

## 배경

NetworkTransform의 지속 복제 필드는 위치 XYZ뿐. 회전은 스폰 시점 1회 전파 후 영구 동결(포탑·차량 등 회전 지속 시나리오 미지원), 스케일은 스폰 와이어에 부재. 후발 합류도 합류 시점 스냅샷 후 동결. decisions/0013이 예고한 "필요 시 확장" 트리거가 충족됨.

## 결정

1. **옵트인 확장** — `SyncRotation`(기본 false)·`SyncScale`(기본 false). 기본 off: 기존 게임의 대역폭·동작을 몰래 바꾸지 않는다.
2. **회전** — `[Replicated(Notify)] float _rx/_ry/_rz/_rw` 4성분 비압축(최소3성분 압축은 커스텀 Serializer+스폰 와이어(비압축 quat)와 표현 불일치로 기각). 서버가 transform.rotation 샘플, 리모트는 SnapshotBuffer<Quaternion>+SlerpQ 보간(위치의 적응형 딜레이·도착 페이싱 재사용). 각변위 스냅은 turn-rate EMA 게이트(웜업 전 비무장 — 스냅 경로 EMA 동결로 보간이 영구 죽는 것을 방지; 계획식 그대로면 정상 회전 두 번째 샘플부터 스냅 오판).
3. **적용 정책** — 회전 notify는 서버 무시·오너/리모트 적용(회전은 예측 대상이 아니며 position notify의 IsOwner 되돌림 가드와 의도적으로 다름 — 전용서버 모델에서 서버가 회전 구동). 쿼터니언은 delta당 1회 소비(`_rotPending` — 4성분 분할 notify를 µs 간격으로 읽으면 비단위 쿼터니언이 보임).
4. **스케일** — `SyncScale` + float 3, 보간 없이 즉시 적용(이산 변화).
5. **텔레포트** — `SetNetworkRotation(Quaternion)` 신설(서버 전용, 위치 무접촉 — `SetNetworkPosition`과 독립). SetNetworkPosition은 위치 전용임을 문서화.
6. **후발 합류** — SendSpawnState가 현시점 회전을 전파하므로 복제 필드 추가만으로 캐치업+지속 델타가 자동 연결.
7. **Awake 무조건 시드** — 회전·스케일 복제 필드를 스폰 변환에서 시드(플래그 오프 시 이후 불변 — 0 대역폭). (0,0,0,0) 쿼터니언 방지.

## 기각한 대안

- 기본 on: 기존 게임 몰래 변경.
- 최소3성분 압축: 표현 불일치+복잡도 — 별도 항목화.
- 오너 되돌림 가드 재사용: 회전은 예측 대상이 아님.

## 결과

- 사용자 가시 API: `SyncRotation`/`SyncScale`/`SetNetworkRotation`. [Replicated] 필드 7개 추가는 생성기 범용 순회로 자동 커버(Emitter 무변경).
- 검증: EditMode 282/282(job `f7a8a607` — T1~T9 포함)·PlayMode 47/47(job `5911c0bc`).
- 함정 문서화: 테스트 어셈블리 NetworkBehaviour 픽스처 금지(GEN-003 주기 발견 — Gen003EnumFixtures.cs 주석).
